// SysManager · DiskAnalyzerService — folder-level disk space analysis
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Analyzes disk usage by scanning top-level subfolders of a given root
/// and computing their total size. Read-only — never modifies anything.
/// </summary>
public sealed class DiskAnalyzerService
{
    public sealed record AnalysisProgress(int FoldersScanned, string CurrentFolder);

    // Skip system subtrees that are slow or inaccessible.
    private static readonly string[] SkipSegments =
    {
        @"\$recycle.bin", @"\system volume information",
        @"\windows\winsxs", @"\windows\csc"
    };

    public Task<IReadOnlyList<DiskUsageEntry>> AnalyzeAsync(
        string rootPath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Analyze(rootPath, progress, ct), ct);

    private static IReadOnlyList<DiskUsageEntry> Analyze(
        string rootPath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return Array.Empty<DiskUsageEntry>();

        string[] topDirs;
        try { topDirs = Directory.GetDirectories(rootPath); }
        catch { return Array.Empty<DiskUsageEntry>(); }

        var results = new List<DiskUsageEntry>();
        int scanned = 0;

        // Also measure loose files in root
        long rootFilesSize = 0;
        int rootFileCount = 0;
        try
        {
            foreach (var f in Directory.GetFiles(rootPath))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    rootFilesSize += new FileInfo(f).Length;
                    rootFileCount++;
                }
                catch { }
            }
        }
        catch { }

        foreach (var dir in topDirs)
        {
            if (ct.IsCancellationRequested) break;
            if (ShouldSkip(dir)) continue;

            scanned++;
            progress?.Report(new AnalysisProgress(scanned, Path.GetFileName(dir)));

            var (size, files, folders) = MeasureFolder(dir, ct);
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) name = dir;

            results.Add(new DiskUsageEntry
            {
                Name = name,
                FullPath = dir,
                SizeBytes = size,
                FileCount = files,
                FolderCount = folders,
                IsAccessDenied = size == 0 && files == 0 && folders == 0
            });
        }

        if (rootFileCount > 0)
        {
            results.Add(new DiskUsageEntry
            {
                Name = "(files in root)",
                FullPath = rootPath,
                SizeBytes = rootFilesSize,
                FileCount = rootFileCount,
                FolderCount = 0
            });
        }

        // Sort descending by size
        results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        // Calculate percentages
        long total = results.Sum(r => r.SizeBytes);
        if (total > 0)
        {
            foreach (var r in results)
                r.Percentage = Math.Round(r.SizeBytes * 100.0 / total, 1);
        }

        progress?.Report(new AnalysisProgress(scanned, "Done"));
        return results;
    }

    private static (long size, int files, int folders) MeasureFolder(string path, CancellationToken ct)
    {
        long totalSize = 0;
        int fileCount = 0;
        int folderCount = 0;

        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var current = stack.Pop();

            string[] files = Array.Empty<string>();
            string[] dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(current); } catch { }
            try { dirs = Directory.GetDirectories(current); } catch { }

            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    totalSize += new FileInfo(f).Length;
                    fileCount++;
                }
                catch { }
            }

            foreach (var d in dirs)
            {
                folderCount++;
                stack.Push(d);
            }
        }

        return (totalSize, fileCount, folderCount);
    }

    private static bool ShouldSkip(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var seg in SkipSegments)
            if (lower.Contains(seg)) return true;
        return false;
    }
}
