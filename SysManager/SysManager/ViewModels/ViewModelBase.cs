// SysManager · ViewModelBase
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progress; // 0-100
    [ObservableProperty] private bool _isProgressIndeterminate;
}
