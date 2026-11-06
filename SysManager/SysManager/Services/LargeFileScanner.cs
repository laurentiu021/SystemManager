// SysManager · LargeFileScanner — read-only biggest-files discovery
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Finds the biggest files on a drive or folder. Read-only — this scanner
/// never modifies or deletes anything. The UI only offers "Show in Explorer"
/// and "Copy path" on the results.
/// </summary>
public sealed class LargeFileScanner
{
    /// <summary>Progress payload: files scanned, bytes scanned, current folder.</summary>
    public sealed record LargeFileProgress(long FilesScanned, long BytesScanned, string CurrentFolder);

    public Task<IReadOnlyList<LargeFileEntry>> ScanAsync(
        string rootPath,
        long minSizeBytes,
        int top = 100,
        IProgress<LargeFileProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(rootPath, minSizeBytes, top, progress, ct), ct);

    private static IReadOnlyList<LargeFileEntry> Scan(
        string rootPath,
        long minSizeBytes,
        int top,
        IProgress<LargeFileProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return [];

        // Guard the traversal ROOT, not just its children. The user picks this folder, so a junction or
        // symlink here walks straight into its target and lists files OUTSIDE the folder they chose — and
        // the results carry a "Show in Explorer" action, so what is listed is what they act on. Every walk
        // in the cleanup services closes this; this one did not (#2381). IsReachedThroughLink, not
        // IsReparsePoint: Windows resolves a link in an ANCESTOR folder before reporting attributes, so a
        // junction one level above the picked root reported False here and the walk silently followed it
        // (#3001). Fails closed on an unreadable path, which is the same answer as "do not walk it".
        if (SafeFileWalk.IsReachedThroughLink(rootPath))
            return [];

        // A non-positive Top has no meaning (nothing to keep) and would crash the
        // eviction path: on the first eligible file, heap.Count (0) < top (<=0) is
        // false, so heap.Min on the empty set returns default ((0, null)) and
        // meta.Remove(null) throws ArgumentNullException. TopCount is bound from an
        // unvalidated UI textbox, so guard it here at the boundary.
        if (top <= 0)
            return [];

        var biggest = new Biggest(top);

        var stack = new Stack<string>();
        stack.Push(rootPath);
        long scanned = 0;
        long bytesScanned = 0;
        // Zero, not the current tick. The throttle below is "not more often than every 200 ms", and
        // seeding it with now made it "not before 200 ms have passed" as well — so a scan that finished
        // inside that window reported nothing at all and the panel showed 0 files, 0 bytes and a blank
        // folder for its whole duration (#2273). It bites harder here than in the sibling scanner because
        // the report sits at the end of each DIRECTORY's loop, so a shallow tree gets only a handful of
        // opportunities and all of them can fall in the first window. TickCount64 is time since boot, so
        // the first directory always clears the gap and every one after it is throttled.
        var lastReport = 0L;

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var cur = stack.Pop();
            if (ShouldSkip(cur)) continue;

            // One listing of the folder, its files and its folders together, each with the attributes and length the
            // OS listed it with, so neither a file's reparse check nor a folder's costs a stat. The folder was listed
            // twice, files then folders, with a File.GetAttributes for every folder on top (#2854). The options are
            // the ones GetFiles used: hidden and system entries are listed.
            FileSystemInfo[] entries = [];
            // A folder that cannot be listed, for either reason, is skipped and the scan goes on.
            try { entries = new DirectoryInfo(cur).GetFileSystemInfos(); }
            catch (IOException) { /* unreadable: skipped */ }
            catch (UnauthorizedAccessException) { /* denied: skipped */ }

            foreach (var entry in entries)
            {
                if (entry is not FileInfo fi) continue;
                if (ct.IsCancellationRequested) break;

                // A link is not a big file. FileInfo.Length reports its TARGET's size, so a link would be
                // listed among the biggest files on the drive while occupying almost nothing — and the row
                // the user acts on would point at the link rather than the thing taking the space (#2381).
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                // Before the counters, not after: a file this tab will never offer is not one it scanned, and
                // leaving 40 GB of paging files in "bytes scanned" would describe a population the results do
                // not come from. Matches the sibling scanner, which also increments only past this check.
                if (ShouldSkipFile(fi.Name)) continue;

                scanned++;

                try
                {
                    var f = fi.FullName;
                    bytesScanned += fi.Length;

                    if (fi.Length < minSizeBytes) continue;

                    biggest.Offer(new LargeFileEntry
                    {
                        Path = f,
                        Name = fi.Name,
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTime
                    });
                }
                catch (IOException) { /* gone or unreadable since the listing: left out */ }
                catch (UnauthorizedAccessException) { /* denied: left out */ }
            }

            // Throttle progress to every ~200 ms so the UI doesn't drown in events.
            var now = Environment.TickCount64;
            if (now - lastReport >= 200)
            {
                progress?.Report(new LargeFileProgress(scanned, bytesScanned, cur));
                lastReport = now;
            }

            // A link to a folder is not walked: what it holds lives where it points (#2381).
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    stack.Push(entry.FullName);
            }
        }

        // A cancelled scan exits the loop with partial results; surfacing them as finished
        // would mislead the user. Throw so the caller's cancel branch handles it.
        ct.ThrowIfCancellationRequested();

        // One last report so the counts settle on their final numbers, with an EMPTY folder — it used to
        // say "Done", and the consumer renders CurrentFolder verbatim into a line that names the folder
        // being scanned, so the panel showed a folder called "Done" (#2273). Empty is the honest value:
        // these are the final counts and no folder is being scanned any more. Nothing has to recognise a
        // sentinel to avoid displaying it.
        progress?.Report(new LargeFileProgress(scanned, bytesScanned, string.Empty));
        return biggest.Largest();
    }

    /// <summary>The <c>top</c> largest files offered so far, each by its own path.</summary>
    /// <remarks>
    /// Both collections compare paths ordinally, as Windows returns them. The set was ordinal and the details were kept
    /// case-insensitively, and a folder can be case-sensitive (WSL makes them, and fsutil turns it on), so a.bin and
    /// A.bin shared one entry: the second overwrote the first, and the list showed one file twice, or one of them was
    /// pushed out and took the other's entry with it, and the scan ended in the app's error handler (#2777).
    /// </remarks>
    internal sealed class Biggest(int top)
    {
        private readonly SortedSet<(long Size, string Path)> _heap = new(Comparer<(long, string)>.Create(
            (a, b) =>
            {
                var c = a.Item1.CompareTo(b.Item1);
                return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
            }));

        private readonly Dictionary<string, LargeFileEntry> _entries = new(StringComparer.Ordinal);

        /// <summary>Keeps <paramref name="file"/> when it is among the largest so far.</summary>
        public void Offer(LargeFileEntry file)
        {
            if (_heap.Count >= top)
            {
                var smallest = _heap.Min;
                if (file.SizeBytes <= smallest.Size) return;
                _heap.Remove(smallest);
                _entries.Remove(smallest.Path);
            }
            _heap.Add((file.SizeBytes, file.Path));
            _entries[file.Path] = file;
        }

        /// <summary>The files kept, largest first.</summary>
        public LargeFileEntry[] Largest() => [.. _heap.Reverse().Select(h => _entries[h.Path])];
    }

    // The system folders and files Duplicate Finder leaves out too, held once in ScanExclusions.
    private static bool ShouldSkip(string path) => path.AsSpan().ContainsAny(ScanExclusions.SystemFolders);

    private static bool ShouldSkipFile(string name) => ScanExclusions.WindowsManagedFiles.Contains(name);
}
