// SysManager · AppPackage
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

public partial class AppPackage : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _currentVersion = string.Empty;
    [ObservableProperty] private string _availableVersion = string.Empty;
    [ObservableProperty] private string _source = "winget";
    [ObservableProperty] private string _status = "Pending";
}
