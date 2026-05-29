// SysManager · DashboardAlert — represents a system alert on the Dashboard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

public enum AlertSeverity { Green, Yellow, Red }
public enum AlertLoadingState { Loading, Complete }

public sealed partial class DashboardAlert : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private AlertSeverity _severity = AlertSeverity.Green;
    [ObservableProperty] private AlertLoadingState _state = AlertLoadingState.Loading;
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private bool _showEta;
}
