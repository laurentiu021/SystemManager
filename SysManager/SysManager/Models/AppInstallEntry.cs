// SysManager · AppInstallEntry — model for a detected application install
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a newly detected application installation.
/// </summary>
public partial class AppInstallEntry : ObservableObject
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
}
