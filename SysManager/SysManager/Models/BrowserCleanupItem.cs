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
public sealed partial class BrowserCleanupItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private int _fileCount;

    public required string Browser { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }

    /// <summary>Absolute paths (files and/or directories) this item covers.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>True for cookie/session data — cleaning it signs you out of sites.</summary>
    public bool IsSensitive { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);
}
