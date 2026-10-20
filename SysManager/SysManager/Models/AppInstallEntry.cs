// SysManager · AppInstallEntry — model for a detected application install
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a newly detected application installation.
/// </summary>
public sealed partial class AppInstallEntry : ObservableObject
{
    /// <summary>Application display name.</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>Publisher or vendor name.</summary>
    [ObservableProperty] private string _publisher = "";

    /// <summary>Install location path.</summary>
    [ObservableProperty] private string _installPath = "";

    /// <summary>When the installation was detected.</summary>
    [ObservableProperty] private DateTime _detectedAt;

    /// <summary>Source of detection (FileSystem, Registry).</summary>
    [ObservableProperty] private string _source = "";

    /// <summary>Whether this alert has been acknowledged by the user.</summary>
    [ObservableProperty] private bool _isAcknowledged;

    /// <summary>
    /// For an uninstall entry, the folders its uninstaller and its icon are in, beside <see cref="InstallPath"/>. Read
    /// with the entry to tell which new folder it belongs to, and not kept.
    /// </summary>
    internal IReadOnlyList<string> Locations { get; init; } = [];
}
