// SysManager · CleanupAge — how recent a file may be and still be removed by a clean-up
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Helpers;

/// <summary>How recent a file may be and still be removed, decided once for every clean-up that has an age.</summary>
internal static class CleanupAge
{
    /// <summary>
    /// How long a temporary file is left alone after it was created or last written: a week, as Windows' own Disk
    /// Cleanup leaves temporary files.
    /// </summary>
    /// <remarks>
    /// Being held open was the only thing that kept a temp file, so an installer that had unpacked its files and not
    /// opened them yet lost them part-way, and could fail or leave a program half-installed (#2890).
    /// </remarks>
    internal static readonly TimeSpan TempFilesKeptFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether <paramref name="entry"/> was created or last written at or after <paramref name="cutoffUtc"/>, so a
    /// clean-up with that cutoff leaves it alone.
    /// </summary>
    /// <remarks>
    /// Both times, not the last write alone: a file unpacked from an archive often keeps the time it had in the
    /// archive, so one created a minute ago can read as years old by its last write.
    /// </remarks>
    internal static bool IsRecent(FileInfo entry, DateTime cutoffUtc) =>
        entry.CreationTimeUtc >= cutoffUtc || entry.LastWriteTimeUtc >= cutoffUtc;

    /// <summary>Whether <paramref name="folder"/> was created at or after <paramref name="cutoffUtc"/>.</summary>
    /// <remarks>
    /// Its creation alone: a folder's last write moves whenever a file in it comes or goes, so a folder the clean-up
    /// had just emptied would read as recent and stay.
    /// </remarks>
    internal static bool IsRecent(DirectoryInfo folder, DateTime cutoffUtc) => folder.CreationTimeUtc >= cutoffUtc;
}
