// SysManager · DiskScanSnapshot — a remembered disk-analysis result for one scanned root
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One remembered Disk Analyzer scan, keyed by the root that was scanned. Persisted so the tab can
/// answer the question a one-off number cannot — "what changed since last time?" — with a delta line.
/// <para>Deliberately small: the measured total plus the top folders by size, not the full tree. The
/// payload is bounded on write (a capped number of roots, a capped number of folders each) so a user
/// who drills into many folders cannot grow the file without limit.</para>
/// <para>This is a serialization DTO — plain settable properties so <c>System.Text.Json</c> can
/// round-trip it. It is machine-specific by nature (absolute paths and sizes on THIS disk), which is
/// why it is NOT part of Profile Export: a delta from another PC's disk would be meaningless here.</para>
/// </summary>
public sealed class DiskScanSnapshot
{
    /// <summary>The scanned root, e.g. <c>C:\Users\me\Downloads</c>. The lookup key.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>When this scan was taken. Shown to the user as the "since" date in the delta line.</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>Sum of the measured folders — the same partial total the tab shows, by the same rules.</summary>
    public long TotalSize { get; set; }

    /// <summary>The largest folders under the root at capture time, most recent scan wins.</summary>
    public List<FolderUsage> TopFolders { get; set; } = [];
}

/// <summary>One folder's measured size within a <see cref="DiskScanSnapshot"/>.</summary>
public sealed class FolderUsage
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
}
