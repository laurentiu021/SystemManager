// SysManager · CleanupCategory / LargeFileEntry models
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// One bucket of safe-to-delete files, shown as a selectable row in the
/// Deep Cleanup view. Mutable so checkbox state can two-way bind.
/// </summary>
public sealed partial class CleanupCategory : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public long TotalSizeBytes { get; init; }
    public int FileCount { get; init; }
    public int SkippedCount { get; init; }
    public TimeSpan? OlderThan { get; init; }
    public bool IsDestructiveHint { get; init; }

    /// <summary>
    /// True for the Recycle Bin category, which must be emptied through the shell
    /// API (SHEmptyRecycleBin) rather than the generic file-delete path — deleting
    /// the per-SID <c>$Recycle.Bin</c> contents directly corrupts the bin's state.
    /// </summary>
    public bool IsRecycleBin { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(TotalSizeBytes);
    public string CountDisplay => SkippedCount > 0 ? $"{FileCount:N0} files · {SkippedCount:N0} skipped" : $"{FileCount:N0} files";
}

public sealed class CleanupResult
{
    public long BytesFreed { get; init; }
    public int FilesDeleted { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string Summary =>
        $"Freed {FormatHelper.FormatSize(BytesFreed)} across {FilesDeleted:N0} files" +
        (Errors.Count > 0 ? $" · {Errors.Count} skipped" : string.Empty);
}

/// <summary>Single large file surfaced by the size scanner.</summary>
public sealed class LargeFileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);
    public string LastModifiedDisplay => LastModified.ToString("dd MMM yyyy");
}
