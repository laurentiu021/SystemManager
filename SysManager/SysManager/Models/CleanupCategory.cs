// SysManager · CleanupCategory / LargeFileEntry models
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// One bucket of safe-to-delete files, shown as a selectable row in the
/// Deep Cleanup view. Mutable so checkbox state can two-way bind.
/// </summary>
public sealed partial class CleanupCategory : ObservableObject, Helpers.ISelectableRow
{
    [ObservableProperty] private bool _isSelected;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public long TotalSizeBytes { get; init; }

    /// <summary>The files in the category. For the Recycle Bin, the items in it, as Explorer counts them.</summary>
    public int FileCount { get; init; }
    public int SkippedCount { get; init; }
    public TimeSpan? OlderThan { get; init; }

    /// <summary>
    /// When set, only files matching one of these wildcards belong to this category — and so only they
    /// are deleted, and its folders are left in place.
    /// </summary>
    /// <remarks>
    /// Carried here rather than looked up at clean time, for the same reason as <see cref="OlderThan"/>:
    /// the cleaner is handed categories and walks their <see cref="Paths"/>, so a filter it cannot see is
    /// a category that reports one set of files and deletes another. Two of the buckets need it — the
    /// blue-screen dumps (<c>*.dmp</c>, among <c>.etl</c> traces) and the Explorer thumbnail cache, whose
    /// folder also holds the jump lists that are the user's recent-files history (#1577).
    /// </remarks>
    public IReadOnlyList<string>? FilePatterns { get; init; }

    public bool IsDestructiveHint { get; init; }

    /// <summary>
    /// True for the Recycle Bin category, which must be emptied through the shell
    /// API (SHEmptyRecycleBin) rather than the generic file-delete path — deleting
    /// the per-SID <c>$Recycle.Bin</c> contents directly corrupts the bin's state.
    /// </summary>
    public bool IsRecycleBin { get; init; }

    /// <summary>
    /// True for the Windows Update download cache and the Delivery Optimization cache. Cleaning one also takes
    /// the system-modification lock (#2510).
    /// </summary>
    /// <remarks>
    /// Two operations elsewhere in SysManager use <c>%WinDir%\SoftwareDistribution</c> and hold that lock. A
    /// Windows Update install reads its downloaded packages from the cache while it runs. Reset Windows Update
    /// renames the whole folder, and the rename fails while anything inside it is open. Deleting there at the same
    /// time as either one could remove packages an install still needs, or fail the reset.
    /// <para>Current Windows keeps the Delivery Optimization cache in the Network Service profile instead, and it
    /// keeps the flag: Windows Update downloads through it (#2602).</para>
    /// </remarks>
    public bool IsWindowsUpdateCache { get; init; }

    /// <summary>
    /// True for the Delivery Optimization cache, which is emptied through Windows'
    /// <c>Delete-DeliveryOptimizationCache</c> rather than the generic file-delete path (#2602).
    /// </summary>
    /// <remarks>
    /// The files belong to the Delivery Optimization service, and that command empties the cache through the
    /// service. Carried here, like <see cref="IsRecycleBin"/>, because the cleaner is handed categories rather than
    /// looking them up.
    /// </remarks>
    public bool IsDeliveryOptimizationCache { get; init; }

    /// <summary>
    /// True for Windows.old, which is removed through Windows' own "Previous Installations" cleanup rather than the
    /// generic file-delete path (#2720).
    /// </summary>
    /// <remarks>
    /// Most of a previous installation is owned by TrustedInstaller, so a delete file by file is refused for most of
    /// it, and what it does remove leaves a rollback that no longer works. Carried here, like
    /// <see cref="IsDeliveryOptimizationCache"/>, because the cleaner is handed categories rather than looking them up.
    /// </remarks>
    public bool IsPreviousWindowsInstallation { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(TotalSizeBytes);

    /// <summary>
    /// "412 files", or "412 files · 3 skipped". The Recycle Bin counts items, as Explorer does: a deleted folder is
    /// one, however many files it holds.
    /// </summary>
    public string CountDisplay
    {
        get
        {
            var counted = string.Create(CultureInfo.InvariantCulture, $"{FileCount:N0} {(IsRecycleBin ? "items" : "files")}");
            return SkippedCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{counted} · {SkippedCount:N0} skipped")
                : counted;
        }
    }
}

public sealed record CleanupResult
{
    public long BytesFreed { get; init; }

    /// <summary>The files deleted, not counting the Recycle Bin's items, which are in <see cref="RecycleBinEmptied"/>.</summary>
    public int FilesDeleted { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// What the Recycle Bin held when this clean emptied it, as the shell counted it. Null when the clean did not empty
    /// it, or it was already empty. Its bytes are in <see cref="BytesFreed"/> as well.
    /// </summary>
    public RecycleBinContents? RecycleBinEmptied { get; init; }

    /// <summary>
    /// True when the clean was cancelled part-way. What it removed before it stopped is in the counts, and is gone for
    /// good: a cancelled clean used to throw, and the counts went with the exception, so the tab said only "Clean
    /// cancelled." and recorded nothing (#2736).
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>Whether the clean removed anything at all: a file, or what the Recycle Bin held.</summary>
    public bool RemovedAnything => FilesDeleted > 0 || BytesFreed > 0 || RecycleBinEmptied is not null;

    /// <summary>
    /// The result in one line, which Deep Cleanup also writes to Recent activity. An emptied Recycle Bin is named,
    /// because it is the part of a clean someone may later come looking for, and a cancelled clean says that it
    /// stopped, with what it had already removed.
    /// </summary>
    public string Summary
    {
        get
        {
            if (Cancelled && !RemovedAnything) return "Cancelled before anything was removed";

            var freed = FormatHelper.FormatSize(BytesFreed);
            var what = (FilesDeleted, RecycleBinEmptied) switch
            {
                (0, { } bin) => $"{freed} by emptying the Recycle Bin ({bin.Display})",
                (var files, { } bin) => string.Create(CultureInfo.InvariantCulture,
                    $"{freed} across {files:N0} files, and emptied the Recycle Bin ({bin.Display})"),
                (var files, null) => string.Create(CultureInfo.InvariantCulture, $"{freed} across {files:N0} files"),
            };
            var line = Cancelled ? $"Cancelled after it freed {what}" : $"Freed {what}";
            return Errors.Count > 0 ? $"{line} · {Errors.Count} skipped" : line;
        }
    }
}

/// <summary>Single large file surfaced by the size scanner.</summary>
public sealed record LargeFileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);
    public string LastModifiedDisplay => LastModified.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
}
