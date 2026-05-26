// SysManager · PingViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.SkiaSharpView;
using Serilog;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>
/// Live ping monitoring: targets, presets, latency chart, health verdict.
/// Delegates shared state (targets, buffers, pinger) to <see cref="NetworkSharedState"/>.
/// </summary>
public sealed partial class PingViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    public ConsoleViewModel Console { get; } = new();

    public PingViewModel(NetworkSharedState shared)
    {
        Shared = shared;
        Shared.Pinger.SampleReceived += OnPingSample;
    }

    private void OnPingSample(PingSample sample)
    {
        var line = sample.LatencyMs.HasValue
            ? PowerShellLine.Output($"Reply from {sample.Host}: time={sample.LatencyMs.Value:F0}ms")
            : PowerShellLine.Warn($"Request timed out. ({sample.Host})");
        Console.Append(line);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shared.Pinger.SampleReceived -= OnPingSample;
        }
        base.Dispose(disposing);
    }
}
