// SysManager · CleanupCategory / LargeFileEntry models
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// One bucket of safe-to-delete files, shown as a selectable row in the
/// Deep Cleanup view. Mutable so checkbox state can two-way bind.
/// </summary>
public partial class CleanupCategory : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public long TotalSizeBytes { get; init; }
    public int FileCount { get; init; }
    public TimeSpan? OlderThan { get; init; }
    public bool IsDestructiveHint { get; init; }

    public string SizeDisplay => HumanSize(TotalSizeBytes);
    public string CountDisplay => $"{FileCount:N0} files";

    public static string HumanSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

public sealed class CleanupResult
{
    public long BytesFreed { get; init; }
    public int FilesDeleted { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string Summary =>
        $"Freed {CleanupCategory.HumanSize(BytesFreed)} across {FilesDeleted:N0} files" +
        (Errors.Count > 0 ? $" · {Errors.Count} skipped" : string.Empty);
}

/// <summary>Single large file surfaced by the size scanner.</summary>
public sealed class LargeFileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
    public string SizeDisplay => CleanupCategory.HumanSize(SizeBytes);
    public string LastModifiedDisplay => LastModified.ToString("dd MMM yyyy");
}
