// SysManager · FileShredderService — secure file deletion with multi-pass overwrite
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Specifies the number of overwrite passes for secure file deletion.
/// </summary>
public enum ShredMethod
{
    /// <summary>1 pass — zero fill.</summary>
    Quick = 1,

    /// <summary>3 passes — 0x00, 0xFF, random.</summary>
    Standard = 3,

    /// <summary>7 passes — alternating patterns + random.</summary>
    Thorough = 7
}

/// <summary>
/// A file whose contents were overwritten and truncated, and whose name Windows would not remove.
/// </summary>
/// <remarks>
/// Its own type so a folder shred counts the file as destroyed: what it held is gone, only its name is left. As a
/// plain <see cref="IOException"/> it was listed among the files "left in place (not deleted)", which reads as
/// untouched.
/// </remarks>
internal sealed class OverwrittenNotRemovedException(string message) : IOException(message);

/// <summary>
/// Thrown when a write or flush fails partway through an overwrite that had already begun — a full disk
/// or a removed drive, not cancellation. The data already written is unrecoverable at that point exactly
/// as it is after a cancellation past the first byte, so the file is still truncated and removed; this
/// carries how many passes actually completed rather than letting a bare <see cref="IOException"/> read as
/// "nothing happened" when the file was, in fact, already destroyed (#2824).
/// </summary>
internal sealed class PartialShredException(int passesRun, string message, Exception inner)
    : IOException(message, inner)
{
    internal int PassesRun { get; } = passesRun;
}

/// <summary>
/// Securely deletes files by overwriting their contents with specified patterns
/// before removing them from the file system.
/// </summary>
public sealed partial class FileShredderService
{
    private const int BufferSize = 65_536; // 64 KB

    private static readonly string[] ProtectedPrefixes = GetProtectedPrefixes();

    private readonly Func<string, FileStream> _openExclusiveWrite;
    private readonly string[] _protectedRoots;
    private readonly string[] _heldRoots;

    public FileShredderService() : this(openExclusiveWrite: null) { }

    /// <summary>
    /// Test seam: opens the exclusive write handle through a supplied factory instead of a real
    /// <see cref="FileStream"/>. A mid-pass write fault (a full disk, a removed drive) cannot be produced
    /// on demand against a real file without touching real system state (a disk quota, a removed volume),
    /// which is exactly what tests here must not do — a test instead supplies a <see cref="FileStream"/>
    /// subclass that opens the real file but throws from an overridden <c>WriteAsync</c> after N calls
    /// (#2824).
    /// </summary>
    /// <param name="protectedRoots">
    /// Null for the system roots. A test names a temp folder instead, so a link test's victim is a file it made
    /// rather than one of the machine's own: aimed at the real hosts file, a regression in both path checks would
    /// have overwritten and deleted it on any elevated run (#2740).
    /// </param>
    /// <param name="heldRoots">
    /// Null for the folders no shred may be, or hold (<see cref="HeldRoots"/>). Files inside them can be shredded. A
    /// test names a temp folder instead.
    /// </param>
    internal FileShredderService(
        Func<string, FileStream>? openExclusiveWrite,
        IReadOnlyList<string>? protectedRoots = null,
        IReadOnlyList<string>? heldRoots = null)
    {
        _openExclusiveWrite = openExclusiveWrite ?? DefaultOpenExclusiveWrite;
        _protectedRoots = protectedRoots is null ? ProtectedPrefixes : [.. protectedRoots];
        _heldRoots = heldRoots is null ? HeldRoots() : [.. heldRoots];
    }

    /// <summary>
    /// The folders a shred may never be, or hold: the profile, the folder of all profiles, AppData and its parts,
    /// ProgramData, and the person's own folders (Documents, Downloads, Desktop and the rest, wherever they were
    /// moved).
    /// </summary>
    /// <remarks>
    /// The folders inside them are the person's to shred, and so are their files. These themselves are not: a shred of
    /// the profile, or of a drive folder that holds a moved Downloads, destroyed everything in it after a question
    /// that counted one item. The leftover search refuses to offer the same folders (LeftoverFinder.ProtectedFolders).
    /// </remarks>
    private static string[] HeldRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = string.IsNullOrEmpty(local) ? null : Path.GetDirectoryName(local);
        string?[] roots =
        [
            profile,
            string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            appData,
            appData is null ? null : Path.Combine(appData, "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        return [.. roots.Concat(KnownFolders.UserFolders().Select(f => f.Path)).OfType<string>()
            .Where(r => r.Length > 0)];
    }

    /// <summary>
    /// How a folder shred walks the folder picked, and how the queue sizes it: through a link above it, since the
    /// folder was picked by that name, and into none inside it.
    /// </summary>
    /// <remarks>
    /// The protected-roots check on the canonical path, not a refusal, is what keeps that safe (#2814). The file walk
    /// used the default, which refuses a root reached through a link: a folder under a junction or on a SUBST drive
    /// was walked as empty and reported shredded, and the queue sized it at 0 bytes. No sink here: each shred adds
    /// its own with <c>with</c>, so no two share a list.
    /// </remarks>
    internal static SafeWalkOptions FolderWalk { get; } = new() { RefuseRootReachedThroughLink = false };

    private static FileStream DefaultOpenExclusiveWrite(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Write,
        FileShare.None,
        BufferSize,
        FileOptions.WriteThrough | FileOptions.SequentialScan);

    private static string[] GetProtectedPrefixes()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys32 = Path.Combine(winDir, "System32");
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return [winDir, sys32, progFiles, progFilesX86];
    }

    /// <summary>
    /// Securely shreds a single file using the specified method, returning how many overwrite passes
    /// actually ran. Fewer than <paramref name="method"/> asked for means the user cancelled after the
    /// overwrite had begun — see the cancellation rule below.
    /// </summary>
    /// <remarks>
    /// CANCELLATION: the token stops work that has not started; it does not abandon an overwrite in
    /// progress. Before the first byte is written the file is untouched, so cancelling throws and the file
    /// survives. After it, the original bytes are gone and no outcome can hand them back — stopping there
    /// left a file at its original name and length holding <c>0x00</c> while the UI reported "Cancelled",
    /// which is the one thing a shredder must never do (#2374). So the pass in flight always finishes, the
    /// remaining passes are dropped, and the file is truncated and removed. The extra work is bounded by the
    /// remainder of a single pass, and what is left on disk then matches what the caller is told.
    /// </remarks>
    public async Task<int> ShredFileAsync(string filePath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePath(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        var attrs = File.GetAttributes(filePath);

        // Refuse a link outright, rather than following it: ShredFolderAsync already refuses a link
        // folder as the ROOT it was given, but a link named directly here had no equivalent check and
        // was followed straight through — the passes landed on the TARGET's bytes, and only the link
        // itself was removed, leaving the real file at the target truncated to zero (#2824). ValidatePath
        // above only blocks a link whose target sits under a protected system root; an ordinary target
        // outside the user's selection is exactly what this refuses in addition.
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
        {
            var target = ResolveFinalPath(filePath) ?? "a location that could not be resolved";
            throw new SecurityException(
                $"\"{Path.GetFileName(filePath)}\" is a link; shredding it would overwrite and delete data "
                + $"at its target, outside the selected location: {target}");
        }

        // Refuse a compressed, sparse or EFS-encrypted file before opening it at all: NTFS keeps these in
        // a form where changed data goes to NEWLY ALLOCATED clusters before the old ones are freed, so an
        // ordinary write handle's passes land on the new clusters while the original bytes stay readable
        // in the freed space — the same reason Sysinternals' SDelete has to find and overwrite those
        // clusters directly instead of writing through a handle like this one's (#2824).
        if (DescribeWhyNotOverwritable(attrs) is { } why)
            throw new IOException(
                $"\"{Path.GetFileName(filePath)}\" is {why}. NTFS writes changed data for files like this to "
                + "new disk clusters rather than overwriting in place, so a secure overwrite cannot reach the "
                + "original bytes — they would stay readable in the space the old clusters occupied. Remove "
                + "that attribute first, or delete it normally and rely on full-disk encryption (BitLocker) to "
                + "protect what remains on the drive.");

        // Cleared so the write handle can open, and set again if not a byte gets overwritten after all.
        using var readOnly = ClearedReadOnly.From(filePath, attrs);

        var totalPasses = (int)method;
        var patterns = GetPatterns(method);

        // Passes completed end to end, and whether a single byte has been written. Together they decide
        // what a cancellation is allowed to do: throw while the file is still the user's, finish the job
        // once it is not.
        var passesRun = 0;
        var overwriteBegan = false;

        // Set once a write-time fault (a full disk, a removed drive) hits after at least one byte of a
        // pass was already written. Declared here, not inside the block below, so it is still visible at
        // the throw site after the exclusive handle has been disposed and the file removed.
        IOException? writeFault = null;

        // The file the held handle is open on, read once its path has been checked, so the removal at the end
        // acts on that file and on nothing a changed path leads to instead.
        FileIdentity? shredded = null;

        // Open the file ONCE with an exclusive (FileShare.None) write handle and keep it open
        // for every pass and the final truncate. This closes the validate-by-path/act-by-path
        // TOCTOU: ValidatePath above checks a path STRING, but the destructive writes must act
        // on the exact object we validated. By holding one exclusive handle and re-verifying
        // THAT HANDLE's canonical path (GetFinalPathNameByHandle) against the protected roots
        // before writing a byte, a reparse point swapped in after ValidatePath can no longer
        // redirect the overwrite — the handle is already bound to the resolved object, and
        // FileShare.None blocks anyone from moving/replacing it mid-shred.
        // The explicit block scopes the `await using` so the exclusive handle is disposed
        // BEFORE the removal below, which opens the file again for deletion.
        {
            await using var stream = _openExclusiveWrite(filePath);

            // Re-validate the identity of the object we actually hold. If the resolved path
            // sits under a protected root (a reparse point swapped in between ValidatePath and
            // this open pointed us there), refuse — the exclusive handle guarantees this is the
            // same object we will overwrite, so there is no residual race.
            var heldCanonical = GetFinalPathFromHandle(stream.SafeFileHandle);
            if (heldCanonical is not null)
            {
                // The path checked may have been a local one, a drive letter mapped to a share say, or a folder
                // swapped for a link to one since; where the handle actually is decides.
                ThrowIfOnANetworkShare(heldCanonical);
                ThrowIfProtected(ExpandShortPath(heldCanonical));
            }

            // Which file this handle is open on. The file is removed at the end through a handle checked to be open
            // on the same one, never by its name alone.
            shredded = FileIdentity.OfAnyFileSystem(stream.SafeFileHandle);

            // Refuse to overwrite a file that has more than one hard link. Overwriting the
            // bytes destroys the data for EVERY name that points at this file — including links
            // outside the selected scope, or a hard link a standard user placed at an unprotected
            // path that shares its data with a protected file (the path guard sees only the name
            // we opened, and GetFinalPathNameByHandle resolves symlinks/junctions but NOT hard
            // links). Unlinking a single name is not a secure shred either — the data survives via
            // the other links — so refuse outright. Best-effort: if the query fails we proceed
            // (a normal single-link file is the overwhelming case), never blocking a real shred.
            if (FileIdentity.LinksOf(stream.SafeFileHandle) is > 1 and var links)
                throw new IOException(
                    $"This file has {links} hard links, so its data is shared with other locations. " +
                    "Securely shredding it would destroy that shared data — remove the extra links first, or delete it normally.");

            var fileLength = stream.Length;

            for (var pass = 0; pass < totalPasses; pass++)
            {
                // Between passes is the only place a cancellation can be honoured without leaving a
                // half-overwritten file behind. If nothing has been written the file is intact, so throw
                // and leave it. Otherwise stop after this point: the data is already unrecoverable, so the
                // honest end state is a removed file rather than a corrupt one kept (see the remarks above).
                if (ct.IsCancellationRequested)
                {
                    if (!overwriteBegan) ct.ThrowIfCancellationRequested();
                    break;
                }

                stream.Position = 0; // rewind for this pass (same handle, no reopen)

                var buffer = new byte[BufferSize];
                var pattern = patterns[pass];

                if (pattern is null)
                {
                    // Random pass — use cryptographic PRNG for secure overwrite
                    RandomNumberGenerator.Fill(buffer);
                }
                else
                {
                    Array.Fill(buffer, pattern.Value);
                }

                var bytesRemaining = fileLength;
                try
                {
                    while (bytesRemaining > 0)
                    {
                        // Only while the file is still intact. Past the first chunk, a mid-pass abort is what
                        // produced the silently-corrupted file: the leading chunks zeroed, the tail still the
                        // user's plaintext, and the operation reported as cancelled.
                        if (!overwriteBegan) ct.ThrowIfCancellationRequested();

                        var writeSize = (int)Math.Min(BufferSize, bytesRemaining);

                        // Re-randomize buffer each chunk for random passes
                        if (pattern is null)
                            RandomNumberGenerator.Fill(buffer.AsSpan(0, writeSize));

                        // CancellationToken.None, deliberately: the token governs whether a pass STARTS, never
                        // whether it finishes. A cancelled WriteAsync would tear the pass exactly where the
                        // check above refuses to.
                        await stream.WriteAsync(buffer.AsMemory(0, writeSize), CancellationToken.None).ConfigureAwait(false);
                        overwriteBegan = true;
                        readOnly?.Overwritten();
                        bytesRemaining -= writeSize;
                    }

                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException ex) when (overwriteBegan)
                {
                    // Before the first byte, an IOException means the file is untouched — let it propagate
                    // normally (the `when` filter above leaves that case uncaught here). Once overwriteBegan,
                    // the only safe move left is the one cancellation already takes: stop, and let the
                    // unconditional cleanup below still run.
                    writeFault = ex;
                    break;
                }

                passesRun++;

                var overallProgress = (int)((pass + 1) * 100.0 / totalPasses);
                progress?.Report(overallProgress);
            }

            // Final truncate on the SAME held handle — no by-path reopen. Unconditional: a token cancelled
            // during the last pass used to throw out of this flush, leaving the file truncated to zero and
            // never deleted, which is the worst of both outcomes. Runs whether the loop above finished,
            // stopped on cancellation, or stopped on writeFault — all three leave data that is already
            // unrecoverable and must still be removed.
            stream.SetLength(0);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            // Named streams (e.g. the Zone.Identifier Windows adds to every downloaded file, recording
            // where it came from) share the unnamed one's directory entry but each has a handle of its own.
            // The overwrite above never touches them, and the removal below takes them unoverwritten right
            // alongside the file (#2824). Overwritten while the exclusive handle is still held, which no
            // named stream's own handle is refused by, so the file cannot be moved or replaced in between and
            // the streams written are this file's.
            await OverwriteNamedStreamsAsync(filePath, patterns).ConfigureAwait(false);
        } // `await using` disposes the exclusive handle here.

        // The file contents are already securely overwritten and truncated to zero at
        // this point, so the shred guarantee holds even if the directory-entry removal
        // fails. Surface a removal failure as a clear IOException rather than letting a
        // raw one escape — the caller can report "overwritten but not removed".
        RemoveShreddedFile(filePath, shredded);

        if (writeFault is not null)
        {
            // Thrown only now, after the delete above has actually run: a caller catching this can trust
            // the file is already gone, the same guarantee a normal return gives.
            // No {Path} and no attached exception object: writeFault.Message (e.g. "Access to the path
            // '...' is denied") is exactly the evidence a shred is meant to remove, and it would survive
            // into the log — the sink renders a logged exception's full text, not just this template's
            // tokens (#2796). Type + HResult is enough to diagnose a write fault without naming the file.
            Log.Warning(
                "Write error partway through a pass after the overwrite had begun; truncated and removed "
                + "with {PassesRun} of {Total} pass(es) completed. {ExceptionType} (HResult {HResult})",
                passesRun, totalPasses, writeFault.GetType().Name, writeFault.HResult);
            throw new PartialShredException(passesRun,
                $"A write error stopped the overwrite after {passesRun} of {totalPasses} pass(es), but the data "
                + "already written is unrecoverable — the file was removed rather than left half-overwritten.",
                writeFault);
        }

        // No {Path}: the name and location of a file the user asked to destroy must not survive into a
        // log kept for weeks and bundled into a bug report (#2796).
        if (passesRun < totalPasses)
            Log.Information(
                "File shredded with {Passes} of {Total} passes after cancellation, then removed.",
                passesRun, totalPasses);
        else
            Log.Information("File shredded successfully ({Method}).", method);

        return passesRun;
    }

    /// <summary>
    /// Removes the file the shred overwrote, through a handle checked to be open on that same file.
    /// </summary>
    /// <remarks>
    /// The overwrite holds one exclusive handle from the check of what it opened to the truncate, and the file could
    /// not be moved or replaced while it was held. The delete came after that handle closed and went by the name,
    /// so it removed whatever the path led to by then, with no check of what that was, and it was the one step not
    /// bound to the file that was checked and written. Now the name is opened again for deletion, a link at it as the
    /// link rather than followed, and the file goes only when that handle is open on the file the overwrite held.
    /// When it is not, nothing is removed, and the caller hears that the contents were overwritten and the name
    /// stayed.
    /// </remarks>
    private static void RemoveShreddedFile(string filePath, FileIdentity? shredded)
    {
        using var handle = NativeMethods.CreateFile(
            ExtendedPathIfNeeded(filePath),
            NativeMethods.DELETE | NativeMethods.FILE_READ_ATTRIBUTES,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (handle.IsInvalid) throw NotRemoved(Marshal.GetLastPInvokeError());

        if (shredded is null || FileIdentity.OfAnyFileSystem(handle) != shredded)
            throw new OverwrittenNotRemovedException(
                "File contents were securely overwritten, but the file could not be removed: its name no longer "
                + "leads to the file that was overwritten, so nothing was removed.");

        var disposition = new NativeMethods.FileDispositionInfo { DeleteFile = 1 };
        if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfoClass, ref disposition,
                (uint)Marshal.SizeOf<NativeMethods.FileDispositionInfo>()))
            throw NotRemoved(Marshal.GetLastPInvokeError());
    }

    /// <summary>The failure a removal refused with Windows error <paramref name="error"/> is reported as.</summary>
    private static OverwrittenNotRemovedException NotRemoved(int error)
    {
        var why = new System.ComponentModel.Win32Exception(error).Message;
        return new OverwrittenNotRemovedException(error == NativeMethods.ERROR_ACCESS_DENIED
            ? $"File contents were securely overwritten, but removal was denied: {why}"
            : $"File contents were securely overwritten, but the file could not be removed: {why}");
    }

    /// <summary>
    /// Overwrites every NAMED stream on <paramref name="filePath"/> with <paramref name="patterns"/> —
    /// never the unnamed <c>::$DATA</c> one, which the caller already handled. Best-effort: a stream
    /// that cannot be opened is left for the delete that follows to remove, same as the main stream
    /// would be if this were a normal file the shred could not reach at all.
    /// </summary>
    /// <remarks>
    /// Measured: opening a named stream with <see cref="FileShare.None"/> succeeds even while the
    /// unnamed stream is held exclusively, since they are independent handles on disk sharing one directory
    /// entry. So this runs while the main handle is still held, which keeps the file at its name until
    /// every stream of it has been written. Enumeration and overwrite both run to completion unconditionally,
    /// and no cancellation token is threaded in here: by the time this is reached the main data is already
    /// unrecoverable,
    /// these streams are typically a few hundred bytes (Windows' own <c>Zone.Identifier</c> is one line
    /// of text), and the delete that follows removes every stream together regardless of how far this
    /// got (#2824).
    /// </remarks>
    private async Task OverwriteNamedStreamsAsync(string filePath, byte?[] patterns)
    {
        using var handle = NativeMethods.FindFirstStream(ExtendedPathIfNeeded(filePath), 0, out var data, 0);
        if (handle.IsInvalid)
        {
            // ERROR_HANDLE_EOF is "no stream at all". Anything else is a search that failed, and its named streams go
            // with the file when it is removed but are not overwritten first; the log says so, without the name.
            var error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ERROR_HANDLE_EOF)
                Log.Warning("Could not list a file's named streams, so they were not overwritten. (Error {Error})",
                    error);
            return;
        }

        do
        {
            if (IsNamedDataStream(data.cStreamName))
                await OverwriteOneStreamAsync(filePath + data.cStreamName, patterns).ConfigureAwait(false);
        } while (NativeMethods.FindNextStream(handle, out data));
    }

    /// <summary>
    /// True for a named <c>$DATA</c> stream such as <c>:Zone.Identifier:$DATA</c> — never the unnamed
    /// <c>::$DATA</c>, and never one of the rarer non-<c>$DATA</c> stream types this does not try to
    /// overwrite (their content is not file data in the sense a secure erase promises to reach).
    /// </summary>
    internal static bool IsNamedDataStream(string streamName) =>
        streamName != "::$DATA" && streamName.EndsWith(":$DATA", StringComparison.Ordinal);

    /// <summary>Overwrites one already-identified stream in place, then truncates it to zero.</summary>
    private async Task OverwriteOneStreamAsync(string streamPath, byte?[] patterns)
    {
        try
        {
            await using var stream = _openExclusiveWrite(streamPath);
            var length = stream.Length;
            var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(length, 1))];

            foreach (var pattern in patterns)
            {
                stream.Position = 0;
                if (pattern is null) RandomNumberGenerator.Fill(buffer);
                else Array.Fill(buffer, pattern.Value);

                var bytesRemaining = length;
                while (bytesRemaining > 0)
                {
                    var writeSize = (int)Math.Min(buffer.Length, bytesRemaining);
                    if (pattern is null) RandomNumberGenerator.Fill(buffer.AsSpan(0, writeSize));
                    await stream.WriteAsync(buffer.AsMemory(0, writeSize), CancellationToken.None).ConfigureAwait(false);
                    bytesRemaining -= writeSize;
                }

                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            stream.SetLength(0);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // No {Stream} and no attached exception — the stream path names the file it belongs to (#2796).
            Log.Debug(
                "Could not overwrite a named stream, leaving it for the delete to remove. {ExceptionType} (HResult {HResult})",
                ex.GetType().Name, ex.HResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(
                "Access denied overwriting a named stream, leaving it for the delete to remove. {ExceptionType} (HResult {HResult})",
                ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Securely shreds all files in a folder recursively, then removes the folder.
    /// Skips junctions and symlinks to prevent shredding outside the target directory.
    /// </summary>
    public async Task<ShredFolderReport> ShredFolderAsync(string folderPath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ValidatePath(folderPath, requireCanonical: true);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        // Refuse a reparse-point root outright, rather than relying on SafeFileWalk quietly yielding
        // nothing for one. The walk declining to enter it is correct but silent, and for a shred the
        // difference matters: the user asked for this folder to be destroyed, so "there was nothing to do"
        // is the wrong answer to "that is a link to data outside your selection". They must pick the real
        // target. (SecurityException is already the shred services' "not allowed" signal and is handled by
        // the caller.)
        if ((new DirectoryInfo(folderPath).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException(
                $"The selected folder is a junction or symlink; shredding it would destroy data at its target outside the selected location: {folderPath}");

        // Links the walk refuses to follow. Collected rather than silently dropped: see ShredFolderReport.
        List<string> skippedLinks = [];
        // Folders inside it the walk could not open: what they hold was never reached, so it is still there.
        HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
        var files = SafeFileWalk
            .Files(folderPath, ct, FolderWalk with { SkippedLinks = skippedLinks, Unreadable = unreadable })
            .ToList();
        var totalFiles = files.Count;

        // Files whose secure overwrite did NOT complete (denied, locked, hard-linked, …). We
        // must never fall back to a plain delete for these: the caller believes a shredded file
        // was securely overwritten, so a plain-deleted (recoverable) file would silently break
        // that guarantee. We leave them on disk and report them instead of force-deleting.
        List<string> failedFiles = [];

        // Files destroyed before the user cancelled. Reported rather than thrown away: every one of them
        // is gone, so ending with the folder marked "Cancelled" would tell the user nothing happened to
        // data that no longer exists (#2374).
        var cancelled = false;
        var shredded = 0;

        // Files a write-time fault stopped partway (fewer passes than the method asked for), not the
        // files in failedFiles above: these ARE gone, counted in shredded too, just not with the full
        // method. A separate tally so the report can say so rather than implying every file got the
        // overwrite depth the user picked (#2824).
        var partiallyOverwritten = 0;

        // Files overwritten whose names Windows would not remove: destroyed, counted in shredded too, and still listed
        // in the folder, so it cannot be gone.
        var notRemoved = 0;

        // A file refused as a place no shred may touch. The folders above it are then left as they are, emptied
        // ones included: something below them led where it should not have.
        var refused = false;

        for (var i = 0; i < totalFiles; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            try
            {
                // ShredFileAsync clears ReadOnly itself, and sets it again on a file it then leaves (#2757).
                await ShredFileAsync(files[i], method, null, ct).ConfigureAwait(false);
                shredded++;
            }
            catch (OperationCanceledException)
            {
                // Cancelled before this file's first byte, so it is untouched. ShredFileAsync no longer
                // abandons one it has started, which is why this arm cannot leave a corrupt file behind.
                cancelled = true;
                break;
            }
            catch (PartialShredException ex)
            {
                // Caught ahead of the plain IOException arm below: this file IS gone — a write fault
                // stopped the overwrite after it had begun, and ShredFileAsync already finished the
                // truncate-and-delete before throwing. Counting it as failed would make a folder shred
                // report the file as still on disk when it is not.
                // No {Path} and no attached exception in any of the three arms below: the file name is
                // exactly the evidence a shred is meant to remove (#2796).
                Log.Warning(
                    "Write error partway through, file destroyed with fewer passes. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
                shredded++;
                partiallyOverwritten++;
            }
            catch (OverwrittenNotRemovedException ex)
            {
                // Ahead of the IOException arm below, for the reason PartialShredException is: the contents are gone.
                Log.Warning(
                    "Overwritten, but the name could not be removed. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
                shredded++;
                notRemoved++;
            }
            catch (SecurityException ex)
            {
                // Thrown only before a byte is written (the path check, a link, the held handle's check), so the file
                // is as it was. It escaped the loop before, and the count of files already destroyed went with it.
                Log.Warning(
                    "A file was refused before its overwrite. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
                failedFiles.Add(files[i]);
                refused = true;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(
                    "Access denied while shredding a file. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
                failedFiles.Add(files[i]);
            }
            catch (IOException ex)
            {
                Log.Warning(
                    "I/O error while shredding a file. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
                failedFiles.Add(files[i]);
            }

            var overallProgress = (int)((i + 1) * 100.0 / totalFiles);
            progress?.Report(overallProgress);
        }

        if (cancelled)
        {
            // No directory cleanup and no failure throw on this path. The folder was not emptied, so
            // pruning parts of its structure would change the tree the user still has for no benefit, and
            // an IOException here would replace "you cancelled, N files are gone" with a message about
            // locked files. The report carries both numbers instead.
            // No {Path}: the folder's name and location must not survive into the log (#2796).
            Log.Information(
                "Folder shred cancelled after {Shredded} of {Total} file(s) ({Method}).",
                shredded, totalFiles, method);

            return new ShredFolderReport
            {
                FilesShredded = shredded,
                SkippedLinks = skippedLinks,
                FilesLeftInPlace = failedFiles.Count,
                FilesPartiallyOverwritten = partiallyOverwritten,
                FilesNotRemoved = notRemoved,
                FoldersNotRead = unreadable.Count,
                FolderLeftInPlace = true,
                WasCancelled = true,
            };
        }

        // Remove now-empty directories deepest-first. A securely-shredded file was already
        // deleted by ShredFileAsync, so its directory becomes empty and is removed here; a
        // directory that still holds a file we could NOT shred is left in place (with the file).
        // We only ever remove EMPTY directories (recursive:false), so this can never plain-delete
        // an un-overwritten file — the bug this replaces (the old recursive delete did).
        //
        // The directory list comes from the same SafeFileWalk as the files, so it honours the same reparse
        // boundary — that is the point of sharing it. The framework's recursive enumerator
        // (Directory.EnumerateDirectories with AllDirectories) would instead DESCEND THROUGH a child
        // junction and hand TryRemoveIfEmpty a path at the link's target, letting Directory.Delete remove an
        // empty directory OUTSIDE the selected folder. A folder that contains a child junction is
        // intentionally left in place rather than descended into.
        //
        // No SkippedLinks sink here, deliberately: this walk skips a SUBSET of what the file walk skips
        // (directories only, not link files), so reporting from it as well would under-count and
        // double-count at the same time.
        if (!refused)
        {
            // Every file has been dealt with, so nothing is left for a Cancel to spare: the cleanup runs to its end.
            // With the caller's token, a Cancel during the last file's overwrite ended it before it began, and
            // every emptied folder stayed with nothing said about it.
            foreach (var dir in SafeFileWalk.DirectoriesDeepestFirst(folderPath, CancellationToken.None, FolderWalk))
            {
                TryRemoveIfEmpty(dir);
            }
            TryRemoveIfEmpty(folderPath);
        }

        if (failedFiles.Count > 0)
        {
            // Honest partial failure: the folder was NOT fully shredded. Report which files were
            // left in place so the caller (and the user) never believe a file that could not be
            // securely overwritten was destroyed, and how many were, which are gone either way.
            var sample = string.Join(", ", failedFiles.Take(5).Select(Path.GetFileName));
            var more = failedFiles.Count > 5 ? $", and {failedFiles.Count - 5} more" : string.Empty;
            var destroyed = shredded switch
            {
                0 => "",
                1 => " The other file was destroyed.",
                _ => $" The other {shredded} files were destroyed.",
            };
            throw new IOException(
                $"{failedFiles.Count} of {totalFiles} file(s) could not be securely shredded and were left in place "
                + $"(not deleted): {sample}{more}.{destroyed}");
        }

        // A skipped link is NOT a failure — refusing to follow it is the correct behaviour — so it is
        // returned rather than thrown. It still has to reach the user: the folder was not emptied, and
        // they asked for it to be destroyed. Reporting "shredded successfully" while the folder is still
        // sitting there is the one mistake a shredder cannot make.
        var report = new ShredFolderReport
        {
            FilesShredded = shredded,
            SkippedLinks = skippedLinks,
            FilesPartiallyOverwritten = partiallyOverwritten,
            FilesNotRemoved = notRemoved,
            FoldersNotRead = unreadable.Count,
            // Asked of the disk: the cleanup passes over every reason a folder stayed.
            FolderLeftInPlace = FolderStillThere(folderPath),
        };

        // No {Path} here either, same reason (#2796).
        if (!report.IsDone)
            Log.Warning("Folder shred ended with the folder still there ({Method}).", method);
        else if (skippedLinks.Count > 0)
            Log.Information("Folder shredded, {Skipped} link(s) left alone ({Method}).",
                skippedLinks.Count, method);
        else
            Log.Information("Folder shredded successfully ({Method}).", method);

        return report;
    }

    /// <summary>
    /// Removes <paramref name="dir"/> only when it is empty, swallowing the expected
    /// "not empty / denied / already gone" faults. Used to clean up the emptied directory
    /// structure after a folder shred WITHOUT ever force-deleting a file that remains in it.
    /// </summary>
    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                // Windows will not remove a read-only folder, and Properties > Customize makes one: Explorer clears
                // the flag first. Never on a link, which the walk does not hand here but a folder can become since.
                var attributes = File.GetAttributes(dir);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                    File.SetAttributes(dir, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(dir, recursive: false);
            }
        }
        // No {Dir} and no attached exception — a directory left over from a shred names the folder the
        // user asked to destroy (#2796).
        catch (IOException ex)
        {
            Log.Debug("Shredder: could not remove directory. {ExceptionType} (HResult {HResult})", ex.GetType().Name, ex.HResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Shredder: access denied removing directory. {ExceptionType} (HResult {HResult})", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>Whether <paramref name="folderPath"/> is still on disk; true when that cannot be told.</summary>
    private static bool FolderStillThere(string folderPath)
    {
        try { return Directory.Exists(folderPath) || File.Exists(folderPath); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Throws <see cref="SecurityException"/> when <paramref name="path"/>, or where it really leads, is a place no
    /// shred may touch.
    /// </summary>
    /// <param name="requireCanonical">
    /// True for a folder about to be walked: when where it really leads cannot be read, it is refused rather than
    /// trusted. A folder's walk opens what is below it, so its check has to see where it leads; a file is checked again
    /// through the handle its overwrite holds.
    /// </param>
    internal void ValidatePath(string path, bool requireCanonical = false)
    {
        var fullPath = Path.GetFullPath(path);

        // Before anything else, and before any of it reaches out to the share: every check below compares against
        // this PC's own drive-letter paths, and a share leads back to them. \\localhost\C$\Program Files is
        // C:\Program Files, and every resolution step here leaves it in that form, so it passed the protected
        // roots that its drive-letter form is refused by.
        ThrowIfOnANetworkShare(fullPath);

        // Resolve symlinks/junctions before validating. Path.GetFullPath only
        // canonicalizes '.'/'..' — it does NOT follow reparse points, so a symlink
        // at an unprotected location (e.g. C:\temp\link -> C:\Windows\System32\...)
        // would otherwise pass this check and be shredded through. Validate the real
        // target instead. ResolveLinkTarget(true) walks the full chain to the final
        // target; it returns null when the path is not a link.
        try
        {
            var info = new FileInfo(fullPath);
            var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            if (finalTarget is not null)
                fullPath = Path.GetFullPath(finalTarget.FullName);
        }
        catch (IOException) { /* not a link or cannot resolve — validate the literal path */ }
        catch (UnauthorizedAccessException) { /* cannot probe — validate the literal path */ }

        // Expand any 8.3 short components (e.g. C:\PROGRA~1 -> C:\Program Files).
        // Path.GetFullPath does NOT expand short names, so without this a short-name
        // alias of a protected directory would slip past the prefix check below.
        fullPath = ExpandShortPath(fullPath);
        ThrowIfProtected(fullPath);

        // ResolveLinkTarget above only follows a reparse point that sits at the LEAF of
        // the path — it does not collapse a junction/symlink in a PARENT component. So a
        // path like C:\temp\j\notepad.exe, where C:\temp\j is a junction to System32
        // (a junction a standard user can create without admin), still slips past the
        // leaf check above and would be shredded through to the real System32 file.
        // Ask the OS for the fully-resolved canonical path, which collapses EVERY reparse
        // point anywhere in the chain, and validate that too. For a file, when the path can't be opened (missing,
        // locked) the already-validated literal form is kept, and the held handle is checked again before a byte is
        // written. A folder is refused instead: its walk goes on to open everything below it.
        var canonical = ResolveFinalPath(fullPath);
        if (canonical is null)
        {
            if (!requireCanonical) return;
            // A folder that is simply gone says so, as it did before this check was added; the share check above
            // already ran, so this reaches nothing off this PC.
            if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Folder not found: {path}");
            throw new SecurityException(
                $"SysManager could not read where this folder really leads, so it is not shredded: {fullPath}");
        }
        // A link anywhere on the way can lead to a share, with every name of the path itself on this PC.
        ThrowIfOnANetworkShare(canonical);
        if (!canonical.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            ThrowIfProtected(ExpandShortPath(canonical));
    }

    /// <summary>
    /// Throws <see cref="SecurityException"/> when <paramref name="path"/> is on a network share.
    /// </summary>
    /// <remarks>
    /// A file on a share is never shredded. The protected roots are this PC's own drive-letter paths, and a share,
    /// the PC's own <c>C$</c> among them, reaches the same folders by another name. And an overwrite sent over the
    /// network is not a secure one: the computer holding the share writes it where it chooses, and can keep copies
    /// of the file in its caches and snapshots.
    /// </remarks>
    private static void ThrowIfOnANetworkShare(string path)
    {
        if (IsOnANetworkShare(path))
            throw new SecurityException(
                $"Shredding files on a network share is not allowed: {path}. The computer that holds the share "
                + "decides where an overwrite lands and can keep copies of the file, so shred it on that computer.");
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a network share: a UNC path, in any of its spellings, or a drive letter
    /// Windows maps to one.
    /// </summary>
    /// <remarks>
    /// A device path to a volume of this PC (<c>\\?\C:\</c>) is not one: the check of its canonical form sees where
    /// it leads. Decided from the text, and for a drive letter from the drive's kind, so nothing reaches the share.
    /// </remarks>
    internal static bool IsOnANetworkShare(string path)
    {
        var text = path.TrimStart().Replace('/', '\\');
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        if (text.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        try
        {
            return Path.GetPathRoot(text) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Throws <see cref="SecurityException"/> if <paramref name="fullPath"/> equals or sits under any system-protected
    /// root, holds one, is the root of a drive, or is or holds one of the folders <see cref="HeldRoots"/> lists.
    /// Matches on a directory boundary so an unrelated sibling (e.g. "C:\WindowsApps") is not blocked by "C:\Windows".
    /// </summary>
    /// <remarks>
    /// Only "equal or under" was checked, so C:\ itself passed (its literal and canonical forms are both C:\), and so
    /// did a folder that holds Windows or a profile. For a file, and for the held handle's check, the added rules
    /// never apply: a file holds nothing and is no drive's root.
    /// </remarks>
    private void ThrowIfProtected(string fullPath)
    {
        foreach (var prefix in _protectedRoots)
        {
            if (fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    $"Shredding system-protected paths is not allowed: {fullPath}");
            }

            // A folder that holds one of them would take it along.
            if (SystemPaths.IsInsideSubtree(prefix, fullPath))
                throw new SecurityException(
                    $"Shredding a folder that holds Windows' own folders is not allowed: {fullPath}. "
                    + "Pick the folders inside it you want shredded.");
        }

        if (SystemPaths.IsDriveRoot(fullPath))
            throw new SecurityException(
                $"Shredding the whole of a drive is not allowed: {fullPath}. "
                + "Pick the folders on it you want shredded.");

        foreach (var held in _heldRoots)
        {
            if (SystemPaths.IsInsideSubtree(held, fullPath))
                throw new SecurityException(
                    $"Shredding your profile, one of its main folders, or a folder that holds them is not allowed: "
                    + $"{fullPath}. Pick the files and folders inside it you want shredded.");
        }
    }

    /// <summary>
    /// Returns the fully-resolved canonical path for <paramref name="path"/> with every
    /// reparse point (junction/symlink) in the chain collapsed, via
    /// <c>GetFinalPathNameByHandle</c>. Returns null when the path can't be opened
    /// (missing, locked, access denied) so the caller falls back to the literal path.
    /// </summary>
    internal static string? ResolveFinalPath(string path)
    {
        // FILE_FLAG_BACKUP_SEMANTICS lets us open a directory handle too, not just files.
        using SafeFileHandle handle = NativeMethods.CreateFile(
            ExtendedPathIfNeeded(path),
            0, // no access rights needed — we only resolve the name
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        return GetFinalPathFromHandle(handle);
    }

    /// <summary>
    /// Returns the fully-resolved canonical path for an ALREADY-OPEN handle (every reparse
    /// point collapsed), or null if the name can't be read. Used to verify the identity of the
    /// exact handle we hold for the overwrite — closing the validate-by-path / act-by-path
    /// TOCTOU window, since the check is on the same object we then write.
    /// </summary>
    private static string? GetFinalPathFromHandle(SafeFileHandle handle)
    {
        // First call with a zero-length buffer returns the required length (incl. NUL).
        uint needed = NativeMethods.GetFinalPathNameByHandle(handle, [], 0, 0);
        if (needed == 0)
            return null;

        var buffer = new char[needed];
        uint written = NativeMethods.GetFinalPathNameByHandle(handle, buffer, needed, 0);
        if (written == 0 || written >= needed)
            return null;

        var result = new string(buffer, 0, (int)written);

        // GetFinalPathNameByHandle returns the \\?\ (or \\?\UNC\) extended-length prefix.
        // Strip it so the result compares against the plain protected-prefix strings.
        const string dosPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (result.StartsWith(uncPrefix, StringComparison.Ordinal))
            result = @"\\" + result[uncPrefix.Length..];
        else if (result.StartsWith(dosPrefix, StringComparison.Ordinal))
            result = result[dosPrefix.Length..];

        return result;
    }

    /// <summary>
    /// The ReadOnly flag cleared so the write handle can open, set again when the shred returns without having
    /// overwritten a byte: a file refused after the open, or cancelled before its first byte, is left as it was found.
    /// </summary>
    /// <remarks>
    /// It was cleared before the hard-link refusal and the first cancellation check, so a file the shred left
    /// "untouched" had lost it, and attributes belong to the file rather than to a name: a refused hard link lost it
    /// under every name the file has (#2757).
    /// </remarks>
    private sealed class ClearedReadOnly : IDisposable
    {
        private readonly string _path;
        private readonly FileAttributes _attributes;
        private bool _overwritten;

        private ClearedReadOnly(string path, FileAttributes attributes)
        {
            _path = path;
            _attributes = attributes;
        }

        /// <summary>Clears ReadOnly on <paramref name="path"/>, or returns null when it is not set.</summary>
        public static ClearedReadOnly? From(string path, FileAttributes attributes)
        {
            if (!attributes.HasFlag(FileAttributes.ReadOnly)) return null;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return new ClearedReadOnly(path, attributes);
        }

        /// <summary>The overwrite has begun, so the file is no longer the one that was found.</summary>
        public void Overwritten() => _overwritten = true;

        public void Dispose()
        {
            if (_overwritten || !File.Exists(_path)) return;
            // No path in either arm: the file's name is the evidence a shred is meant to remove (#2796).
            try { File.SetAttributes(_path, _attributes); }
            catch (IOException ex)
            {
                Log.Warning("Could not set ReadOnly again on a file the shred left. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning("Could not set ReadOnly again on a file the shred left. {ExceptionType} (HResult {HResult})",
                    ex.GetType().Name, ex.HResult);
            }
        }
    }

    /// <summary>
    /// Names why <paramref name="attrs"/> cannot be securely overwritten in place, or null when there is
    /// no such reason. Checked against bare attributes rather than a live file, so every combination —
    /// including the ones a real compressed/sparse/encrypted file would be awkward to set up for a test —
    /// is directly testable (#2824).
    /// </summary>
    internal static string? DescribeWhyNotOverwritable(FileAttributes attrs) => attrs switch
    {
        _ when attrs.HasFlag(FileAttributes.Encrypted) => "encrypted",
        _ when attrs.HasFlag(FileAttributes.Compressed) => "compressed",
        _ when attrs.HasFlag(FileAttributes.SparseFile) => "a sparse file",
        _ => null,
    };

    /// <summary>
    /// Returns the byte pattern for each pass. A null entry means random fill.
    /// </summary>
    private static byte?[] GetPatterns(ShredMethod method) => method switch
    {
        ShredMethod.Quick => [0x00],
        ShredMethod.Standard => [0x00, 0xFF, null],
        ShredMethod.Thorough => [0x00, 0xFF, 0x00, 0xFF, 0xAA, 0x55, null],
        _ => [0x00]
    };

    /// <summary>
    /// Expands any 8.3 short path components to their long form via GetLongPathName.
    /// Returns the input unchanged if the path doesn't exist or the API fails — the
    /// caller still validates the (possibly short) literal path in that case.
    /// </summary>
    internal static string ExpandShortPath(string path)
    {
        // Fast path: no '~' means there can't be an 8.3 alias component.
        if (!path.Contains('~')) return path;

        // First call with a zero-length buffer returns the required length (incl. NUL).
        var asked = ExtendedPathIfNeeded(path);
        uint needed = NativeMethods.GetLongPathName(asked, [], 0);
        if (needed == 0) return path; // not found / no permission — keep literal

        var buffer = new char[needed];
        uint written = NativeMethods.GetLongPathName(asked, buffer, needed);
        if (written == 0 || written >= needed) return path; // failed / race — keep literal

        // A path asked in its extended form comes back in it; the callers compare plain paths.
        var expanded = new string(buffer, 0, (int)written);
        return !ReferenceEquals(asked, path) && expanded.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? expanded[ExtendedPrefix.Length..]
            : expanded;
    }

    private const string ExtendedPrefix = @"\\?\";

    // MAX_PATH: from this length on, a path given to a Win32 call as it is fails unless the PC has long paths
    // turned on.
    private const int MaxShortPath = 260;

    /// <summary>
    /// <paramref name="path"/> in the <c>\\?\</c> form when a Win32 call needs it to reach the file, or unchanged.
    /// </summary>
    /// <remarks>
    /// The overwrite goes through <see cref="FileStream"/>, which .NET extends for long paths itself, but the
    /// shredder's own calls (the removal, the stream search, the final-path check, the short-name expansion) took
    /// the path as it was. A file whose path had 260 characters or more was overwritten and never removed, and its
    /// named streams were skipped. This applies .NET's own rule (260 or more characters, or a trailing dot or
    /// space), so a shorter path reaches every call exactly as before. A UNC or device path is returned as it is.
    /// <para>The trailing dot or space is read on the path as given: <see cref="Path.GetFullPath(string)"/> drops it,
    /// so the full form would name a different file. Such a name only exists when something made it in the extended
    /// form, and it is reached the same way.</para>
    /// </remarks>
    internal static string ExtendedPathIfNeeded(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
        if ((path.EndsWith('.') || path.EndsWith(' ')) && Path.IsPathFullyQualified(path)) return ExtendedPrefix + path;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
        return full.Length >= MaxShortPath ? ExtendedPrefix + full : path;
    }

    private static partial class NativeMethods
    {
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

        /// <summary>Opens a link itself rather than following it.</summary>
        internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;

        internal const uint DELETE = 0x0001_0000;
        internal const uint FILE_READ_ATTRIBUTES = 0x0080;
        internal const int ERROR_ACCESS_DENIED = 5;

        /// <summary>What <c>FindFirstStreamW</c> sets when a file has no stream to list.</summary>
        internal const int ERROR_HANDLE_EOF = 38;

        /// <summary><c>FileDispositionInfo</c> in <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
        internal const int FileDispositionInfoClass = 4;

        /// <summary><c>FILE_DISPOSITION_INFO</c>: <c>DeleteFile</c> set deletes the file on its last close.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo lpFileInformation,
            uint dwBufferSize);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetLongPathNameW")]
        internal static partial uint GetLongPathName(
            string lpszShortPath,
            [Out] char[] lpszLongPath,
            uint cchBuffer);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateFileW", SetLastError = true)]
        internal static partial SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static partial uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            [Out] char[] lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        // FindFirstStreamW/FindNextStreamW stay classic [DllImport]: WIN32_FIND_STREAM_DATA carries a
        // fixed-size string, which [LibraryImport] would need a hand-written marshaller for — the same
        // trade-off RecycleBinHelper's SHFileOperationW makes.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstStreamW")]
        internal static extern SafeFindStreamHandle FindFirstStream(
            string lpFileName, uint infoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindNextStreamW")]
        internal static extern bool FindNextStream(
            SafeFindStreamHandle hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FindClose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WIN32_FIND_STREAM_DATA
        {
            public long StreamSize;

            // MAX_PATH (260) + 36, the documented maximum: a colon, a stream name up to MAX_PATH, a
            // colon, and ":$DATA" or one of the other type suffixes.
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            public string cStreamName;
        }
    }

    /// <summary>A <c>FindFirstStreamW</c> search handle, closed via <c>FindClose</c> like a file-find handle.</summary>
    private sealed class SafeFindStreamHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
    }
}
