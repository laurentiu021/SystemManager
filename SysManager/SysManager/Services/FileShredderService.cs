// SysManager · FileShredderService — secure file deletion with multi-pass overwrite
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using System.Security.Cryptography;
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
public sealed class FileShredderService
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

        var fileLength = new FileInfo(filePath).Length;
        var totalPasses = (int)method;
        var patterns = GetPatterns(method);

        for (var pass = 0; pass < totalPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.WriteThrough | FileOptions.SequentialScan);

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

        // Final cleanup: truncate, flush, close, then delete
        await using (var finalStream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            finalStream.SetLength(0);
            await finalStream.FlushAsync(ct).ConfigureAwait(false);
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

        var files = EnumerateFilesSafe(folderPath);
        var totalFiles = files.Count;

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
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "I/O error while shredding file: {Path}", files[i]);
            }

            var overallProgress = (int)((i + 1) * 100.0 / totalFiles);
            progress?.Report(overallProgress);
        }

        // Remove empty directories bottom-up
        try
        {
            Directory.Delete(folderPath, recursive: true);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Could not fully remove folder structure: {Path}", folderPath);
        }

        Log.Information("Folder shredded successfully: {Path} ({Method})", folderPath, method);
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
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
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

        foreach (var prefix in ProtectedPrefixes)
        {
            // Match on a directory boundary, not a raw prefix: the path must equal the
            // protected root or sit beneath it (prefix + separator). Without this,
            // "C:\Windows" would also block an unrelated sibling like "C:\WindowsApps".
            if (fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    $"Shredding system-protected paths is not allowed: {fullPath}");
            }
        }
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
}
