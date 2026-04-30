// SysManager · DuplicateFileService — find duplicate files by content hash
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Security.Cryptography;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans a folder tree for duplicate files. Two-pass approach:
/// 1. Group files by size (files with unique sizes can't be duplicates).
/// 2. For each size group with 2+ files, compute SHA-256 and group by hash.
///
/// Read-only — never modifies or deletes anything.
/// </summary>
public sealed class DuplicateFileService
{
    /// <summary>Progress payload.</summary>
    public sealed record ScanProgress(
        long FilesDiscovered,
        long FilesHashed,
        long BytesProcessed,
        string CurrentFile,
        string Phase);

    // Skip system subtrees that are slow, protected, or pointless.
    private static readonly string[] SkipSegments =
    {
        @"\$recycle.bin", @"\system volume information", @"\windows\winsxs",
        @"\windows\system32\config", @"\windows\csc"
    };

    // Skip known system files.
    private static readonly string[] SkipFiles =
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys"
    };

    /// <summary>Minimum file size to consider (skip tiny files that are often config/metadata).</summary>
    private const long DefaultMinSize = 1024; // 1 KB

    public Task<IReadOnlyList<DuplicateFileGroup>> ScanAsync(
        string rootPath,
        long minSizeBytes = DefaultMinSize,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(rootPath, minSizeBytes, progress, ct), ct);

    private static IReadOnlyList<DuplicateFileGroup> Scan(
        string rootPath,
        long minSizeBytes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return Array.Empty<DuplicateFileGroup>();

        // ── Pass 1: discover files and group by size ──
        var sizeGroups = new Dictionary<long, List<FileInfo>>();
        long discovered = 0;
        var stack = new Stack<string>();
        stack.Push(rootPath);
        var lastReport = Environment.TickCount64;

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var dir = stack.Pop();
            if (ShouldSkipDir(dir)) continue;

            string[] files = Array.Empty<string>();
            string[] dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { /* skip protected directory */ }
            catch (IOException) { /* skip inaccessible directory */ }
            try { dirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { /* skip protected directory */ }
            catch (IOException) { /* skip inaccessible directory */ }

            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length < minSizeBytes) continue;
                    if (ShouldSkipFile(fi.Name)) continue;

                    discovered++;
                    if (!sizeGroups.TryGetValue(fi.Length, out var list))
                    {
                        list = new List<FileInfo>(2);
                        sizeGroups[fi.Length] = list;
                    }
                    list.Add(fi);

                    var now = Environment.TickCount64;
                    if (now - lastReport >= 200)
                    {
                        progress?.Report(new ScanProgress(discovered, 0, 0, fi.Name, "Discovering files…"));
                        lastReport = now;
                    }
                }
                catch (UnauthorizedAccessException) { /* skip inaccessible file */ }
                catch (IOException) { /* skip inaccessible file */ }
            }

            foreach (var d in dirs) stack.Push(d);
        }

        // ── Pass 2: partial hash pre-filter, then full hash ──
        // First compare only the first 4 KB of each file. Files that differ
        // in the first 4 KB cannot be duplicates, so we skip the expensive
        // full-file hash for them. This dramatically speeds up scans with
        // many large files that share the same size but differ in content.
        var candidates = sizeGroups.Where(g => g.Value.Count >= 2).ToList();
        long hashed = 0;
        long bytesProcessed = 0;
        var hashGroups = new Dictionary<string, DuplicateFileGroup>();

        foreach (var group in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Sub-group by partial hash (first 4 KB)
            var partialGroups = new Dictionary<string, List<FileInfo>>();
            foreach (var fi in group.Value)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var partialHash = ComputePartialHash(fi.FullName, ct);
                    if (!partialGroups.TryGetValue(partialHash, out var pList))
                    {
                        pList = new List<FileInfo>(2);
                        partialGroups[partialHash] = pList;
                    }
                    pList.Add(fi);
                }
                catch (UnauthorizedAccessException) { /* skip inaccessible file during partial hash */ }
                catch (IOException) { /* skip inaccessible file during partial hash */ }
            }

            // Only full-hash files whose partial hashes matched 2+ files
            foreach (var pg in partialGroups.Values)
            {
                if (pg.Count < 2) continue;
                foreach (var fi in pg)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var hash = ComputeHash(fi.FullName, ct);
                        hashed++;
                        bytesProcessed += fi.Length;

                        if (!hashGroups.TryGetValue(hash, out var dg))
                        {
                            dg = new DuplicateFileGroup { Hash = hash, FileSize = fi.Length };
                            hashGroups[hash] = dg;
                        }
                        dg.Files.Add(new DuplicateFileEntry
                        {
                            Path = fi.FullName,
                            Name = fi.Name,
                            SizeBytes = fi.Length,
                            LastModified = fi.LastWriteTime
                        });
                        dg.Count = dg.Files.Count;

                        var now = Environment.TickCount64;
                        if (now - lastReport >= 200)
                        {
                            progress?.Report(new ScanProgress(discovered, hashed, bytesProcessed, fi.Name, "Hashing files…"));
                            lastReport = now;
                        }
                    }
                    catch (UnauthorizedAccessException) { /* skip inaccessible file during full hash */ }
                    catch (IOException) { /* skip inaccessible file during full hash */ }
                }
            }
        }

        progress?.Report(new ScanProgress(discovered, hashed, bytesProcessed, "Done", "Complete"));

        // Only return groups with 2+ files (actual duplicates).
        return hashGroups.Values
            .Where(g => g.Files.Count >= 2)
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    private static string ComputeHash(string filePath, CancellationToken ct)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var sha = SHA256.Create();

        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>
    /// Hash only the first 4 KB of a file. Used as a fast pre-filter:
    /// files that differ in the first 4 KB cannot be duplicates.
    /// </summary>
    private static string ComputePartialHash(string filePath, CancellationToken ct)
    {
        const int partialSize = 4096;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, partialSize);
        var buffer = new byte[partialSize];
        int totalRead = 0;
        int read;
        while (totalRead < partialSize && (read = stream.Read(buffer, totalRead, partialSize - totalRead)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            totalRead += read;
        }
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, totalRead)));
    }

    private static bool ShouldSkipDir(string path)
    {
        var lower = path.ToLowerInvariant();
        return SkipSegments.Any(seg => lower.Contains(seg));
    }

    private static bool ShouldSkipFile(string name)
        => SkipFiles.Any(skip => name.Equals(skip, StringComparison.OrdinalIgnoreCase));
}
