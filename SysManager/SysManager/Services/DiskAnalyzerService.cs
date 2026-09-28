// SysManager · DiskAnalyzerService — folder-level disk space analysis
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Analyzes disk usage by scanning top-level subfolders of a given root
/// and computing their total size. Read-only — never modifies anything.
/// </summary>
public sealed class DiskAnalyzerService
{
    /// <summary>How far the walk has got, reported once per top-level subfolder as it is reached.</summary>
    /// <param name="FoldersScanned">Top-level subfolders started so far. Not a fraction: the total is not
    /// known until the walk ends, which is why the tab shows an indeterminate bar rather than a
    /// percentage.</param>
    /// <param name="CurrentFolder">The FULL PATH of the folder being measured, empty on the settling report
    /// that follows the walk. The consumer trims it for display and shows the path on hover, so a name alone
    /// would leave the hover with nothing to add. Empty rather than a word like "Done" because a caller
    /// rendering this into a sentence would otherwise name a folder that does not exist (#2273, #2274).</param>
    public sealed record AnalysisProgress(int FoldersScanned, string CurrentFolder);

    /// <summary>Why the chosen folder could not be measured at all.</summary>
    public enum AnalysisFailure
    {
        /// <summary>It was measured.</summary>
        None,

        /// <summary>No folder exists at the path any more: renamed, deleted, or on a drive that was removed.</summary>
        NotFound,

        /// <summary>
        /// It is a link to another location, or its attributes could not be read, which is treated the same way.
        /// </summary>
        IsLink,

        /// <summary>Windows did not let SysManager list what is in it.</summary>
        Unreadable,
    }

    /// <summary>One analysis: the folders measured, or why the chosen folder could not be measured.</summary>
    /// <param name="Entries">The folders measured, largest first. Empty for a failure, and for a folder with none.</param>
    /// <param name="Failure"><see cref="AnalysisFailure.None"/> when the folder was measured.</param>
    public sealed record Analysis(IReadOnlyList<DiskUsageEntry> Entries, AnalysisFailure Failure);

    // Skip system subtrees that are slow or inaccessible.
    private static readonly string[] SkipSegments =
    {
        @"\$recycle.bin", @"\system volume information",
        @"\windows\winsxs", @"\windows\csc"
    };

    /// <summary>
    /// The excluded subtrees, in the form a user would recognise, so the UI can say exactly what is
    /// missing from the total instead of duplicating the list above and letting the two drift.
    /// </summary>
    /// <remarks>
    /// These are excluded because they are slow or unreadable, not because they are small —
    /// <c>Windows\WinSxS</c> alone is routinely several gigabytes. A total that omits them without
    /// saying so reads as a bug when the user compares it against the free space Windows reports.
    /// </remarks>
    public static IReadOnlyList<string> ExcludedFolderNames { get; } =
    [
        @"$Recycle.Bin",
        @"System Volume Information",
        @"Windows\WinSxS",
        @"Windows\CSC"
    ];

    /// <summary>Measures the top-level subfolders of <paramref name="rootPath"/>, or says why it could not.</summary>
    /// <remarks>
    /// A folder that could not be measured used to be an empty list, which the tab reported as a finished scan with
    /// no subfolders, and saved as the folder's latest scan. The next real scan then read as "larger than your last
    /// scan" by the whole folder (#2504).
    /// </remarks>
    public Task<Analysis> AnalyzeAsync(
        string rootPath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Analyze(rootPath, progress, ct, Directory.GetDirectories), ct);

    /// <summary><see cref="AnalyzeAsync"/>'s body, handed the top-level listing so each way it fails is testable.</summary>
    /// <remarks>
    /// A real folder cannot be made unreadable on demand everywhere: .NET opens a directory with backup semantics, so an
    /// elevated process whose backup privilege is enabled lists it whatever its permissions say.
    /// </remarks>
    internal static Analysis Analyze(
        string rootPath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct,
        Func<string, string[]> listDirectories)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return new Analysis([], AnalysisFailure.NotFound);

        // Guard the ROOT the user picked, not just the top-level entries inside it. Junctions were already
        // skipped one level down, which reads as complete until the root itself is one: the breakdown then
        // describes a tree somewhere else entirely while naming the folder that was chosen. Read-only, so
        // nothing is destroyed — but a size report about the wrong folder is the only thing this tab does
        // (#2381). SafeFileWalk.IsReparsePoint fails closed, the same answer as "do not walk it".
        if (SafeFileWalk.IsReparsePoint(rootPath))
            return new Analysis([], AnalysisFailure.IsLink);

        string[] topDirs;
        try { topDirs = listDirectories(rootPath); }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied listing directories in {Root}", rootPath);
            return new Analysis([], AnalysisFailure.Unreadable);
        }
        catch (DirectoryNotFoundException)
        {
            // Removed between the existence check above and the listing.
            return new Analysis([], AnalysisFailure.NotFound);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "I/O error listing directories in {Root}", rootPath);
            return new Analysis([], AnalysisFailure.Unreadable);
        }

        List<DiskUsageEntry> results = [];
        int scanned = 0;

        // Also measure loose files in root
        long rootFilesSize = 0;
        int rootFileCount = 0;
        try
        {
            // DirectoryInfo.GetFiles: attributes and length arrive with the listing, so the link check is
            // free and the second stat per file that `new FileInfo(f)` used to cost is gone.
            foreach (var fi in new DirectoryInfo(rootPath).GetFiles())
            {
                if (ct.IsCancellationRequested) break;

                // Skip links for the same reason the directory walk skips junctions: FileInfo.Length reports
                // the TARGET's size, so counting one adds bytes that live somewhere else — and if the target
                // is inside the tree being measured, adds them twice (#2381).
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                try
                {
                    rootFilesSize += fi.Length;
                    rootFileCount++;
                }
                catch (UnauthorizedAccessException) { /* skip inaccessible file */ }
                catch (IOException) { /* skip inaccessible file */ }
            }
        }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Access denied listing root files in {Root}", rootPath); }
        catch (IOException ex) { Log.Debug(ex, "I/O error listing root files in {Root}", rootPath); }

        foreach (var dir in topDirs)
        {
            if (ct.IsCancellationRequested) break;
            if (ShouldSkip(dir)) continue;

            // Skip junctions, symbolic links, and mount points at top level
            try
            {
                var attr = File.GetAttributes(dir);
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            scanned++;
            progress?.Report(new AnalysisProgress(scanned, dir));

            var (size, files, folders, denied) = MeasureFolder(dir, ct);
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) name = dir;

            results.Add(new DiskUsageEntry
            {
                Name = name,
                FullPath = dir,
                SizeBytes = size,
                FileCount = files,
                FolderCount = folders,
                IsAccessDenied = denied
            });
        }

        // If the scan was cancelled mid-traversal, the loop `break`s leave partial
        // results — surfacing them as a completed analysis would mislead the user.
        // Throw so the caller's OperationCanceledException branch handles it.
        ct.ThrowIfCancellationRequested();

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

        // The settling report: the final count, with no folder. It carried the literal "Done", which the tab
        // rendered as "Scanning folder 1234: Done" — a sentence saying the scan is still running and naming a
        // folder that does not exist. Empty gives the consumer nothing fake to display and needs no sentinel
        // to recognise, the same fix as LargeFileScanner (#2273).
        progress?.Report(new AnalysisProgress(scanned, string.Empty));
        return new Analysis(results, AnalysisFailure.None);
    }

    private static (long size, int files, int folders, bool accessDenied) MeasureFolder(string path, CancellationToken ct)
    {
        long totalSize = 0;
        int fileCount = 0;
        int folderCount = 0;
        bool accessDenied = false;

        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var current = stack.Pop();

            // DirectoryInfo.GetFiles: see the root-files loop above — one listing instead of a listing plus
            // a stat per file, and the link check below costs nothing.
            FileInfo[] files = [];
            string[] dirs = [];
            try { files = new DirectoryInfo(current).GetFiles(); }
            catch (UnauthorizedAccessException) { accessDenied = true; }
            catch (IOException) { /* skip inaccessible folder */ }
            try { dirs = Directory.GetDirectories(current); }
            catch (UnauthorizedAccessException) { accessDenied = true; }
            catch (IOException) { /* skip inaccessible folder */ }

            foreach (var fi in files)
            {
                if (ct.IsCancellationRequested) break;

                // A link's Length is its target's, so counting one reports bytes that are not in this
                // folder — and double-counts them when the target is inside the tree being measured. The
                // directory walk below has always skipped junctions for exactly that reason (#2381).
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                try
                {
                    totalSize += fi.Length;
                    fileCount++;
                }
                catch (UnauthorizedAccessException) { accessDenied = true; }
                catch (IOException) { /* skip inaccessible file */ }
            }

            foreach (var d in dirs)
            {
                if (ShouldSkip(d)) continue;

                // Skip junctions, symbolic links, and mount points to avoid
                // double-counting the same files through multiple paths.
                try
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (UnauthorizedAccessException) { accessDenied = true; continue; }
                catch (IOException) { continue; }

                folderCount++;
                stack.Push(d);
            }
        }

        return (totalSize, fileCount, folderCount, accessDenied);
    }

    private static bool ShouldSkip(string path)
    {
        // PERF-006: Use OrdinalIgnoreCase instead of allocating a lowercase copy.
        return SkipSegments.Any(seg => path.Contains(seg, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renders a folder-size breakdown as CSV with a header row.</summary>
    /// <remarks>
    /// Both the formatted size and the raw byte count are exported: the formatted one is what the user read
    /// on screen, and the raw one is what a spreadsheet can sort — "9.8 GB" sorts below "10 MB" as text.
    /// <para>This is the export most likely to contain a field needing quotes, since folder names are chosen
    /// by whoever made them and a comma in one is unremarkable. Hence <see cref="Csv"/> rather than raw
    /// concatenation.</para>
    /// </remarks>
    public static string ToCsv(IEnumerable<DiskUsageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, "Name", "Full path", "Size", "Size (bytes)", "Share %", "Files", "Folders",
            "Access denied");
        foreach (var e in entries)
        {
            Csv.AppendRow(sb,
                e.Name,
                e.FullPath,
                e.SizeDisplay,
                e.SizeBytes.ToString(CultureInfo.InvariantCulture),
                e.Percentage.ToString("F1", CultureInfo.InvariantCulture),
                e.FileCount.ToString(CultureInfo.InvariantCulture),
                e.FolderCount.ToString(CultureInfo.InvariantCulture),
                e.IsAccessDenied ? "yes" : "no");
        }
        return sb.ToString();
    }
}
