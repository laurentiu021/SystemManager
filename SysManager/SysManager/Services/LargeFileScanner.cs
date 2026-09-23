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

    // Skip well-known system subtrees where poking around is slow and pointless.
    private static readonly string[] SkipSegments =
    {
        @"\$recycle.bin", @"\system volume information", @"\windows\winsxs",
        @"\windows\system32\config", @"\windows\csc", @"\pagefile.sys",
        @"\hiberfil.sys", @"\swapfile.sys"
    };

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
        // in the cleanup services closes this; this one did not (#2381). SafeFileWalk.IsReparsePoint fails
        // closed on an unreadable path, which is the same answer as "do not walk it".
        if (SafeFileWalk.IsReparsePoint(rootPath))
            return [];

        // A non-positive Top has no meaning (nothing to keep) and would crash the
        // eviction path: on the first eligible file, heap.Count (0) < top (<=0) is
        // false, so heap.Min on the empty set returns default ((0, null)) and
        // meta.Remove(null) throws ArgumentNullException. TopCount is bound from an
        // unvalidated UI textbox, so guard it here at the boundary.
        if (top <= 0)
            return [];

        var heap = new SortedSet<(long Size, string Path)>(Comparer<(long, string)>.Create(
            (a, b) =>
            {
                var c = a.Item1.CompareTo(b.Item1);
                return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
            }));
        var meta = new Dictionary<string, LargeFileEntry>(StringComparer.OrdinalIgnoreCase);

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

            // DirectoryInfo.GetFiles, not Directory.GetFiles: it returns FileInfo objects whose attributes
            // and length are already populated from the listing the OS returned, so the reparse check below
            // is free and the `new FileInfo(f)` that used to follow — a second stat per file — is gone.
            FileInfo[] files = [];
            string[] dirs = [];
            var curDir = new DirectoryInfo(cur);
            try { files = curDir.GetFiles(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { dirs = Directory.GetDirectories(cur); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            foreach (var fi in files)
            {
                if (ct.IsCancellationRequested) break;

                // A link is not a big file. FileInfo.Length reports its TARGET's size, so a link would be
                // listed among the biggest files on the drive while occupying almost nothing — and the row
                // the user acts on would point at the link rather than the thing taking the space (#2381).
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                scanned++;

                try
                {
                    var f = fi.FullName;
                    bytesScanned += fi.Length;

                    if (fi.Length < minSizeBytes) continue;

                    if (heap.Count < top)
                    {
                        heap.Add((fi.Length, f));
                        meta[f] = new LargeFileEntry
                        {
                            Path = f,
                            Name = fi.Name,
                            SizeBytes = fi.Length,
                            LastModified = fi.LastWriteTime
                        };
                    }
                    else
                    {
                        var smallest = heap.Min;
                        if (fi.Length > smallest.Size)
                        {
                            heap.Remove(smallest);
                            meta.Remove(smallest.Path);
                            heap.Add((fi.Length, f));
                            meta[f] = new LargeFileEntry
                            {
                                Path = f,
                                Name = fi.Name,
                                SizeBytes = fi.Length,
                                LastModified = fi.LastWriteTime
                            };
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // Throttle progress to every ~200 ms so the UI doesn't drown in events.
            var now = Environment.TickCount64;
            if (now - lastReport >= 200)
            {
                progress?.Report(new LargeFileProgress(scanned, bytesScanned, cur));
                lastReport = now;
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

        // A cancelled scan exits the loop with partial results; surfacing them as finished
        // would mislead the user. Throw so the caller's cancel branch handles it.
        ct.ThrowIfCancellationRequested();

        // One last report so the counts settle on their final numbers, with an EMPTY folder — it used to
        // say "Done", and the consumer renders CurrentFolder verbatim into a line that names the folder
        // being scanned, so the panel showed a folder called "Done" (#2273). Empty is the honest value:
        // these are the final counts and no folder is being scanned any more. Nothing has to recognise a
        // sentinel to avoid displaying it.
        progress?.Report(new LargeFileProgress(scanned, bytesScanned, string.Empty));
        return heap.Reverse().Select(h => meta[h.Path]).ToArray();
    }

    private static bool ShouldSkip(string path)
    {
        return SkipSegments.Any(seg => path.Contains(seg, StringComparison.OrdinalIgnoreCase));
    }
}
