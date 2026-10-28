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

    public FileShredderService() : this(openExclusiveWrite: null) { }

    /// <summary>
    /// Test seam: opens the exclusive write handle through a supplied factory instead of a real
    /// <see cref="FileStream"/>. A mid-pass write fault (a full disk, a removed drive) cannot be produced
    /// on demand against a real file without touching real system state (a disk quota, a removed volume),
    /// which is exactly what tests here must not do — a test instead supplies a <see cref="FileStream"/>
    /// subclass that opens the real file but throws from an overridden <c>WriteAsync</c> after N calls
    /// (#2824).
    /// </summary>
    internal FileShredderService(Func<string, FileStream>? openExclusiveWrite) =>
        _openExclusiveWrite = openExclusiveWrite ?? DefaultOpenExclusiveWrite;

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

        if (attrs.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);

        var totalPasses = (int)method;
        var patterns = GetPatterns(method);

        // Passes completed end to end, and whether a single byte has been written. Together they decide
        // what a cancellation is allowed to do: throw while the file is still the user's, finish the job
        // once it is not.
        var passesRun = 0;
        var overwriteBegan = false;

        // Set once a write-time fault (a full disk, a removed drive) hits after at least one byte of a
        // pass was already written. Declared here, not inside the block below, so it is still visible at
        // the throw site after the exclusive handle has been disposed and File.Delete has run.
        IOException? writeFault = null;

        // Open the file ONCE with an exclusive (FileShare.None) write handle and keep it open
        // for every pass and the final truncate. This closes the validate-by-path/act-by-path
        // TOCTOU: ValidatePath above checks a path STRING, but the destructive writes must act
        // on the exact object we validated. By holding one exclusive handle and re-verifying
        // THAT HANDLE's canonical path (GetFinalPathNameByHandle) against the protected roots
        // before writing a byte, a reparse point swapped in after ValidatePath can no longer
        // redirect the overwrite — the handle is already bound to the resolved object, and
        // FileShare.None blocks anyone from moving/replacing it mid-shred.
        // The explicit block scopes the `await using` so the exclusive handle is disposed
        // BEFORE the File.Delete below — deleting while we still hold FileShare.None would fail.
        {
            await using var stream = _openExclusiveWrite(filePath);

            // Re-validate the identity of the object we actually hold. If the resolved path
            // sits under a protected root (a reparse point swapped in between ValidatePath and
            // this open pointed us there), refuse — the exclusive handle guarantees this is the
            // same object we will overwrite, so there is no residual race.
            var heldCanonical = GetFinalPathFromHandle(stream.SafeFileHandle);
            if (heldCanonical is not null)
                ThrowIfProtected(ExpandShortPath(heldCanonical));

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
        } // `await using` disposes the exclusive handle here, releasing FileShare.None for Delete.

        // The file contents are already securely overwritten and truncated to zero at
        // this point, so the shred guarantee holds even if the directory-entry removal
        // fails. Surface a delete failure as a clear IOException rather than letting a
        // raw one escape — the caller can report "overwritten but not removed".
        try
        {
            File.Delete(filePath);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"File contents were securely overwritten, but the file could not be removed: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                $"File contents were securely overwritten, but removal was denied: {ex.Message}", ex);
        }

        if (writeFault is not null)
        {
            // Thrown only now, after the delete above has actually run: a caller catching this can trust
            // the file is already gone, the same guarantee a normal return gives.
            Log.Warning(writeFault,
                "Write error partway through a pass after the overwrite had begun; truncated and removed with {PassesRun} of {Total} pass(es) completed: {Path}",
                passesRun, totalPasses, filePath);
            throw new PartialShredException(passesRun,
                $"A write error stopped the overwrite after {passesRun} of {totalPasses} pass(es), but the data "
                + "already written is unrecoverable — the file was removed rather than left half-overwritten.",
                writeFault);
        }

        if (passesRun < totalPasses)
            Log.Information(
                "File shredded with {Passes} of {Total} passes after cancellation, then removed: {Path}",
                passesRun, totalPasses, filePath);
        else
            Log.Information("File shredded successfully: {Path} ({Method})", filePath, method);

        return passesRun;
    }

    /// <summary>
    /// Securely shreds all files in a folder recursively, then removes the folder.
    /// Skips junctions and symlinks to prevent shredding outside the target directory.
    /// </summary>
    public async Task<ShredFolderReport> ShredFolderAsync(string folderPath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ValidatePath(folderPath);

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
        var files = SafeFileWalk
            .Files(folderPath, ct, new SafeWalkOptions { SkippedLinks = skippedLinks })
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

        for (var i = 0; i < totalFiles; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            try
            {
                // Remove read-only attribute if present
                var attrs = File.GetAttributes(files[i]);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(files[i], attrs & ~FileAttributes.ReadOnly);

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
                Log.Warning(ex, "Write error partway through, file destroyed with fewer passes: {Path}", files[i]);
                shredded++;
                partiallyOverwritten++;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Access denied while shredding file: {Path}", files[i]);
                failedFiles.Add(files[i]);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "I/O error while shredding file: {Path}", files[i]);
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
            Log.Information(
                "Folder shred cancelled after {Shredded} of {Total} file(s): {Path} ({Method})",
                shredded, totalFiles, folderPath, method);

            return new ShredFolderReport
            {
                FilesShredded = shredded,
                SkippedLinks = skippedLinks,
                FilesLeftInPlace = failedFiles.Count,
                FilesPartiallyOverwritten = partiallyOverwritten,
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
        foreach (var dir in SafeFileWalk.DirectoriesDeepestFirst(folderPath, ct, new SafeWalkOptions
        {
            // The ONE caller that must still walk a root reached through a link: the user picked
            // this folder by name and asked for it to be destroyed. What keeps that safe is
            // ValidatePath's protected-roots check on the CANONICAL path, not a refusal — a
            // refusal here would report a completed shred that destroyed nothing (#2814).
            RefuseRootReachedThroughLink = false,
        }))
        {
            TryRemoveIfEmpty(dir);
        }
        TryRemoveIfEmpty(folderPath);

        if (failedFiles.Count > 0)
        {
            // Honest partial failure: the folder was NOT fully shredded. Report which files were
            // left in place so the caller (and the user) never believe a file that could not be
            // securely overwritten was destroyed.
            var sample = string.Join(", ", failedFiles.Take(5).Select(Path.GetFileName));
            var more = failedFiles.Count > 5 ? $", and {failedFiles.Count - 5} more" : string.Empty;
            throw new IOException(
                $"{failedFiles.Count} of {totalFiles} file(s) could not be securely shredded and were left in place (not deleted): {sample}{more}.");
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
        };

        if (skippedLinks.Count > 0)
            Log.Information("Folder shredded, {Skipped} link(s) left alone: {Path} ({Method})",
                skippedLinks.Count, folderPath, method);
        else
            Log.Information("Folder shredded successfully: {Path} ({Method})", folderPath, method);

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
                Directory.Delete(dir, recursive: false);
        }
        catch (IOException ex) { Log.Debug(ex, "Shredder: could not remove directory {Dir}", dir); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Shredder: access denied removing directory {Dir}", dir); }
    }

    private static void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

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
        // point anywhere in the chain, and validate that too. Best-effort: when the path
        // can't be opened (missing, locked) we keep the already-validated literal form.
        var canonical = ResolveFinalPath(fullPath);
        if (canonical is not null && !canonical.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            ThrowIfProtected(ExpandShortPath(canonical));
    }

    /// <summary>
    /// Throws <see cref="SecurityException"/> if <paramref name="fullPath"/> equals or
    /// sits under any system-protected root. Matches on a directory boundary so an
    /// unrelated sibling (e.g. "C:\WindowsApps") is not blocked by "C:\Windows".
    /// </summary>
    private static void ThrowIfProtected(string fullPath)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            if (fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    $"Shredding system-protected paths is not allowed: {fullPath}");
            }
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
            path,
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
        uint needed = NativeMethods.GetLongPathName(path, [], 0);
        if (needed == 0) return path; // not found / no permission — keep literal

        var buffer = new char[needed];
        uint written = NativeMethods.GetLongPathName(path, buffer, needed);
        if (written == 0 || written >= needed) return path; // failed / race — keep literal

        return new string(buffer, 0, (int)written);
    }

    private static partial class NativeMethods
    {
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

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
    }
}
