// SysManager · PingViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.SkiaSharpView;
using Serilog;

namespace SysManager.ViewModels;

/// <summary>
/// Live ping monitoring: targets, presets, latency chart, health verdict.
/// Delegates shared state (targets, buffers, pinger) to <see cref="NetworkSharedState"/>.
/// </summary>
public partial class PingViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }

    public PingViewModel(NetworkSharedState shared)
    {
        Shared = shared;
    }

    [RelayCommand]
    private void Start()
    {
        Shared.StartMonitoring();
        StatusMessage = "Monitoring";
        Log.Information("Ping monitoring started");
    }

    [RelayCommand]
    private void Stop()
    {
        Shared.StopMonitoring();
        StatusMessage = "Stopped";
        Log.Information("Ping monitoring stopped");
    }

    [RelayCommand]
    private void AddCustomTarget() => Shared.AddCustomTarget();

    [RelayCommand]
    private void RemoveTarget(Models.PingTarget? target) => Shared.RemoveTarget(target);

    [RelayCommand]
    private void ClearHistory()
    {
        Shared.ClearHistory();
        StatusMessage = "History cleared";
    }
}
