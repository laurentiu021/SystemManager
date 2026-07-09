// SysManager · FileShredderService — secure file deletion with multi-pass overwrite
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Serilog;

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
/// Securely deletes files by overwriting their contents with specified patterns
/// before removing them from the file system.
/// </summary>
public sealed partial class FileShredderService
{
    private const int BufferSize = 65_536; // 64 KB

    private static readonly string[] ProtectedPrefixes = GetProtectedPrefixes();

    private static string[] GetProtectedPrefixes()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys32 = Path.Combine(winDir, "System32");
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return [winDir, sys32, progFiles, progFilesX86];
    }

    /// <summary>
    /// Securely shreds a single file using the specified method.
    /// </summary>
    public async Task ShredFileAsync(string filePath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePath(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        var totalPasses = (int)method;
        var patterns = GetPatterns(method);

        // Open the file ONCE with an exclusive (FileShare.None) write handle and keep it open
        // for every pass and the final truncate. This closes the validate-by-path/act-by-path
        // TOCTOU: ValidatePath above checks a path STRING, but the destructive writes must act
        // on the exact object we validated. By holding one exclusive handle and re-verifying
        // THAT HANDLE's canonical path (GetFinalPathNameByHandle) against the protected roots
        // before writing a byte, a reparse point swapped in after ValidatePath can no longer
        // redirect the overwrite — the handle is already bound to the resolved object, and
        // FileShare.None blocks anyone from moving/replacing it mid-shred.
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.WriteThrough | FileOptions.SequentialScan);

        try
        {
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
            if (NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var info) && info.NumberOfLinks > 1)
                throw new IOException(
                    $"This file has {info.NumberOfLinks} hard links, so its data is shared with other locations. " +
                    "Securely shredding it would destroy that shared data — remove the extra links first, or delete it normally.");

            var fileLength = stream.Length;

            for (var pass = 0; pass < totalPasses; pass++)
            {
                ct.ThrowIfCancellationRequested();

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
                while (bytesRemaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    var writeSize = (int)Math.Min(BufferSize, bytesRemaining);

                    // Re-randomize buffer each chunk for random passes
                    if (pattern is null)
                        RandomNumberGenerator.Fill(buffer.AsSpan(0, writeSize));

                    await stream.WriteAsync(buffer.AsMemory(0, writeSize), ct).ConfigureAwait(false);
                    bytesRemaining -= writeSize;
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);

                var overallProgress = (int)((pass + 1) * 100.0 / totalPasses);
                progress?.Report(overallProgress);
            }

            // Final truncate on the SAME held handle — no by-path reopen.
            stream.SetLength(0);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

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

        Log.Information("File shredded successfully: {Path} ({Method})", filePath, method);
    }

    /// <summary>
    /// Securely shreds all files in a folder recursively, then removes the folder.
    /// Skips junctions and symlinks to prevent shredding outside the target directory.
    /// </summary>
    public async Task ShredFolderAsync(string folderPath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ValidatePath(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        // A junction/symlink AT THE ROOT would make EnumerateFilesSafe follow it into the
        // link target and shred files OUTSIDE the selected folder — the reparse-point skip in
        // EnumerateFilesSafe only guards CHILD entries, never the root it starts enumerating
        // from. Refuse a reparse-point root; the user must select the real target folder to
        // shred it. (SecurityException is already the shred services' "not allowed" signal and
        // is handled by the caller.)
        if ((new DirectoryInfo(folderPath).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException(
                $"The selected folder is a junction or symlink; shredding it would destroy data at its target outside the selected location: {folderPath}");

        var files = EnumerateFilesSafe(folderPath);
        var totalFiles = files.Count;

        // Files whose secure overwrite did NOT complete (denied, locked, hard-linked, …). We
        // must never fall back to a plain delete for these: the caller believes a shredded file
        // was securely overwritten, so a plain-deleted (recoverable) file would silently break
        // that guarantee. We leave them on disk and report them instead of force-deleting.
        List<string> failedFiles = [];

        for (var i = 0; i < totalFiles; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Remove read-only attribute if present
                var attrs = File.GetAttributes(files[i]);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(files[i], attrs & ~FileAttributes.ReadOnly);

                await ShredFileAsync(files[i], method, null, ct).ConfigureAwait(false);
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

        // Remove now-empty directories deepest-first. A securely-shredded file was already
        // deleted by ShredFileAsync, so its directory becomes empty and is removed here; a
        // directory that still holds a file we could NOT shred is left in place (with the file).
        // We only ever remove EMPTY directories (recursive:false), so this can never plain-delete
        // an un-overwritten file — the bug this replaces (the old recursive delete did).
        //
        // The directory list comes from EnumerateDirectoriesSafe, which skips junctions and
        // symlinks exactly like EnumerateFilesSafe. The framework's recursive enumerator
        // (Directory.EnumerateDirectories with AllDirectories) would instead DESCEND THROUGH a
        // child junction and hand TryRemoveIfEmpty a path at the link's target, letting
        // Directory.Delete remove an empty directory OUTSIDE the selected folder. Sharing the
        // file walk's reparse boundary confines cleanup to the real tree; a folder that contains
        // a child junction is intentionally left in place rather than descended into.
        foreach (var dir in EnumerateDirectoriesSafe(folderPath)
                     .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
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

        Log.Information("Folder shredded successfully: {Path} ({Method})", folderPath, method);
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

    /// <summary>
    /// Recursively enumerates files while skipping directories that are
    /// junctions or symlinks (reparse points) to prevent traversal attacks.
    /// </summary>
    private static List<string> EnumerateFilesSafe(string rootPath)
    {
        List<string> results = [];
        Stack<DirectoryInfo> stack = [];
        stack.Push(new DirectoryInfo(rootPath));

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            FileInfo[] files;
            try { files = dir.GetFiles(); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Shredder: access denied enumerating {Dir}", dir.FullName); continue; }
            catch (IOException ex) { Log.Debug(ex, "Shredder: I/O error enumerating {Dir}", dir.FullName); continue; }

            // Skip symlink/hardlink files: shredding would overwrite the LINK TARGET
            // (which may live outside the selected folder), so only real files in the
            // tree are collected — mirrors the reparse-point skip on directories below.
            foreach (var file in files.Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0))
                results.Add(file.FullName);

            DirectoryInfo[] subDirs;
            try { subDirs = dir.GetDirectories(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            // Skip junctions/symlinks to avoid following reparse points out of the tree.
            foreach (var sub in subDirs.Where(s => (s.Attributes & FileAttributes.ReparsePoint) == 0))
            {
                stack.Push(sub);
            }
        }

        return results;
    }

    /// <summary>
    /// Recursively enumerates sub-directories while skipping junctions and symlinks
    /// (reparse points), mirroring <see cref="EnumerateFilesSafe"/>. Used to clean up the
    /// emptied directory structure after a folder shred WITHOUT ever descending through a
    /// reparse point: a child junction is left untouched, so the deepest-first
    /// <see cref="TryRemoveIfEmpty"/> pass can never reach — and delete — an empty directory
    /// at the link's target, outside the selected folder.
    /// </summary>
    private static List<string> EnumerateDirectoriesSafe(string rootPath)
    {
        List<string> results = [];
        Stack<DirectoryInfo> stack = [];
        stack.Push(new DirectoryInfo(rootPath));

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            DirectoryInfo[] subDirs;
            try { subDirs = dir.GetDirectories(); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Shredder: access denied enumerating {Dir}", dir.FullName); continue; }
            catch (IOException ex) { Log.Debug(ex, "Shredder: I/O error enumerating {Dir}", dir.FullName); continue; }

            // Skip junctions/symlinks — never descend through a reparse point (identical
            // boundary to EnumerateFilesSafe, so cleanup honors the exact scope that was shredded).
            foreach (var sub in subDirs.Where(s => (s.Attributes & FileAttributes.ReparsePoint) == 0))
            {
                results.Add(sub.FullName);
                stack.Push(sub);
            }
        }

        return results;
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        // Layout must match the native BY_HANDLE_FILE_INFORMATION exactly. All fields are plain
        // 4-byte DWORDs — each FILETIME is two DWORDs, expanded here — so the struct is fully
        // blittable. LibraryImport rejects a struct that needs runtime marshalling (SYSLIB1051),
        // and enabling assembly-wide DisableRuntimeMarshalling would break the classic
        // [DllImport] surfaces that DO rely on it. Expanding keeps NumberOfLinks at its native
        // offset (40) with no padding.
        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
