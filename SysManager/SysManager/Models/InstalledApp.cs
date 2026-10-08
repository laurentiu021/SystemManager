// SysManager · InstalledApp — model for installed applications
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// An installed Windows application as reported by winget list.
/// </summary>
public sealed partial class InstalledApp : ObservableObject, Helpers.ISelectableRow
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _sizeBytes;
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _uninstallString = "";
    [ObservableProperty] private string _quietUninstallString = "";

    /// <summary>
    /// The folder the app's uninstall entry names as its install location, or empty. Read when the list is built,
    /// which is before any uninstall: some uninstallers delete that entry first, and the leftover search needs it
    /// afterwards (#1527).
    /// </summary>
    [ObservableProperty] private string _installLocation = "";

    /// <summary>Formatted size for display.</summary>
    public string SizeDisplay => SizeBytes > 0 ? FormatHelper.FormatSize(SizeBytes) : "—";
}
