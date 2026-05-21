// SysManager · FileShredderService — secure file deletion with multi-pass overwrite
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
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
                // Random pass
                Random.Shared.NextBytes(buffer);
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
                    Random.Shared.NextBytes(buffer.AsSpan(0, writeSize));

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

        File.Delete(filePath);
        Log.Information("File shredded successfully: {Path} ({Method})", filePath, method);
    }

    /// <summary>
    /// Securely shreds all files in a folder recursively, then removes the folder.
    /// </summary>
    public async Task ShredFolderAsync(string folderPath, ShredMethod method, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ValidatePath(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
        var totalFiles = files.Length;

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

    private static void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        foreach (var prefix in ProtectedPrefixes)
        {
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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
