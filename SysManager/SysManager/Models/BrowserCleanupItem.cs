// SysManager · BrowserCleanupItem
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// One cleanable browser-data category (a browser + a data type such as Cache or History),
/// with the on-disk size discovered by a scan. <see cref="IsSelected"/> drives which items
/// a clean removes; cookies default to unselected so logins aren't dropped by accident.
/// </summary>
public sealed partial class BrowserCleanupItem : ObservableObject, Helpers.ISelectableRow
{
    /// <summary>What a screen reader calls the grid row, rather than the class's type name (#2785).</summary>
    public override string ToString() => $"{Category} in {Browser}";

    [ObservableProperty] private bool _isSelected;

    // SizeDisplay is computed from this, so a change here has to announce that one too or a row keeps
    // showing the old size. Latent today — the scan sets the size in the object initialiser, before the item
    // reaches the collection — but the field is declared mutable and observable, so the first code to
    // recompute a size in place would silently display a stale one. DiskUsageEntry and InstalledApp, the two
    // other models with a mutable size, both do this; this one did not.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _sizeBytes;
    [ObservableProperty] private int _fileCount;

    public required string Browser { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }

    /// <summary>Absolute paths (files and/or directories) this item covers.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>True for cookie/session data — cleaning it signs you out of sites.</summary>
    public bool IsSensitive { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);

    /// <summary>
    /// The row's checkbox as a screen reader says it: the category and whose it is, "Select Cookies in Chrome (Work)".
    /// Every browser and profile has the same four categories, so the category alone sounded the same on all of them,
    /// for the one that signs you out as much as the rest (#2785).
    /// </summary>
    public string SelectName => $"Select {Category} in {Browser}";
}
