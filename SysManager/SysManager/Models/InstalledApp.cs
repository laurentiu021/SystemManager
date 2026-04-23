// SysManager · InstalledApp — model for installed applications
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// An installed Windows application as reported by winget list.
/// </summary>
public partial class InstalledApp : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _status = "";
}
