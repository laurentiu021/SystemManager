// SysManager · DuplicateFileService — find duplicate files by content hash
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.Cryptography;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans a folder tree for duplicate files. Three-pass approach:
/// 1. Group files by size (files with unique sizes can't be duplicates).
/// 2. Within each size group, pre-filter by a partial hash (first 4 KB) to
///    cheaply rule out files that differ early.
/// 3. Full SHA-256 only on files whose partial hash matched 2+ others, then
///    group by the full hash.
///
/// Read-only — never modifies or deletes anything.
/// </summary>
public sealed class DuplicateFileService
{
    /// <summary>Progress payload.</summary>
    /// <param name="FilesDiscovered">Files seen so far that clear the minimum size.</param>
    /// <param name="FilesHashed">Files whose content has been hashed so far.</param>
    /// <param name="BytesProcessed">Total bytes read by the hashing pass.</param>
    /// <param name="CurrentFile">
    /// The FULL path of the file being read — not its name. A consumer showing a single row wants the leaf
    /// and can take it with <see cref="Path.GetFileName(string)"/>; one that reported only the name could
    /// not go the other way, which left a tooltip promising the path showing the row's own text (#2262).
    /// The one exception is the final report, whose value is the placeholder described on
    /// <see cref="CompletePhase"/>.
    /// </param>
    /// <param name="Phase">Plain-language stage, or <see cref="CompletePhase"/> for the final report.</param>
    public sealed record ScanProgress(
        long FilesDiscovered,
        long FilesHashed,
        long BytesProcessed,
        string CurrentFile,
        string Phase);

    /// <summary>
    /// The <see cref="ScanProgress.Phase"/> of the one final report, sent after the walk finishes so a
    /// consumer sees settled counts. Named because a caller has to be able to recognise it: its
    /// <see cref="ScanProgress.CurrentFile"/> is the placeholder "Done" rather than a file, so a UI that
    /// renders every report verbatim would show a file that does not exist.
    /// </summary>
    internal const string CompletePhase = "Complete";

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
            return [];

        // ── Pass 1: discover files and group by size ──
        Dictionary<long, List<FileInfo>> sizeGroups = [];
        long discovered = 0;
        var stack = new Stack<string>();
        stack.Push(rootPath);

        // Zero, not the current tick: the throttle below is "not more often than every 200 ms", and seeding
        // it with now made that "not before 200 ms have passed" as well. A folder that scans in less than
        // that reported no file at all, so the readout stayed blank for the whole scan — and it also made
        // the report shape untestable, because no test folder is slow enough to produce one. TickCount64 is
        // time since boot, so the first file always clears the gap and every file after it is throttled.
        var lastReport = 0L;

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var dir = stack.Pop();
            if (ShouldSkipDir(dir)) continue;

            string[] files = [];
            string[] dirs = [];
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
                        // The FULL path, not fi.Name: the consumer shows the leaf on its status row and the
                        // whole path on hover, and reporting the name made the hover a copy of the row
                        // (#2262). The same name occurs in many folders, and which folder the scan is in is
                        // the one thing the row cannot show.
                        progress?.Report(new ScanProgress(discovered, 0, 0, fi.FullName, "Discovering files…"));
                        lastReport = now;
                    }
                }
                catch (UnauthorizedAccessException) { /* skip inaccessible file */ }
                catch (IOException) { /* skip inaccessible file */ }
            }

            foreach (var d in dirs)
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                stack.Push(d);
            }
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

        // Reset the throttle at the phase boundary, for the same reason it starts at zero: the phase the
        // consumer announces changes here, and without this the first hashed file is only reported if 200 ms
        // happen to have passed since the last discovered one. On a small folder they have not, so the line
        // said "Hashing files…" while the file beside it was still the last one DISCOVERED — or, if discovery
        // itself was short, showed nothing at all.
        lastReport = 0L;

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
            foreach (var pg in partialGroups.Values.Where(g => g.Count >= 2))
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
                            // Full path, for the reason given at the discovery report above (#2262).
                            progress?.Report(new ScanProgress(discovered, hashed, bytesProcessed, fi.FullName, "Hashing files…"));
                            lastReport = now;
                        }
                    }
                    catch (UnauthorizedAccessException) { /* skip inaccessible file during full hash */ }
                    catch (IOException) { /* skip inaccessible file during full hash */ }
                }
        }

        // A cancelled scan exits the loops above with partial results (they break on
        // cancellation rather than throwing, unlike the mid-hash ThrowIfCancellationRequested).
        // Surfacing those as "Complete" would mislead the user; throw so the caller's cancel
        // branch shows "Scan cancelled." — mirroring LargeFileScanner's finalize.
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(discovered, hashed, bytesProcessed, "Done", CompletePhase));

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
        sha.TransformFinalBlock([], 0, 0);
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
        return SkipSegments.Any(seg => path.Contains(seg, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldSkipFile(string name)
        => SkipFiles.Any(skip => name.Equals(skip, StringComparison.OrdinalIgnoreCase));
}
