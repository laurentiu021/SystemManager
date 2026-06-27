// SysManager · StandbyMemoryViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Standby List Cleaner tab. Shows live physical-memory stats and
/// purges the Windows standby list on demand, or automatically when available RAM drops
/// below a threshold (ISLC-style). Purging is safe/non-destructive but needs admin; auto
/// and manual purge only run while the app is open.
/// </summary>
public sealed partial class StandbyMemoryViewModel : ViewModelBase
{
    private readonly StandbyMemoryService _service;
    private readonly DispatcherTimer? _timer;

    [ObservableProperty] private string _totalDisplay = "—";
    [ObservableProperty] private string _availableDisplay = "—";
    [ObservableProperty] private string _loadDisplay = "—";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _autoPurgeEnabled;
    [ObservableProperty] private double _thresholdMb = 1024; // default 1 GB

    public StandbyMemoryViewModel(StandbyMemoryService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        Refresh();
        StatusMessage = IsElevated
            ? "Purge the standby list to free cached memory, or enable auto-purge."
            : "Memory stats shown below. Purging needs administrator — use \"Run as administrator\".";

        if (System.Windows.Application.Current is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }
    }

    /// <summary>Pure: should auto-purge fire? True when available RAM is below the threshold.</summary>
    public static bool ShouldAutoPurge(double availableMb, double thresholdMb)
        => thresholdMb > 0 && availableMb > 0 && availableMb < thresholdMb;

    [RelayCommand]
    private void Refresh()
    {
        var status = _service.GetMemoryStatus();
        TotalDisplay = status.TotalDisplay;
        AvailableDisplay = status.AvailableDisplay;
        LoadDisplay = status.LoadDisplay;
    }

    private bool _autoPurgeInFlight;

    private async void Tick()
    {
        var status = _service.GetMemoryStatus();
        TotalDisplay = status.TotalDisplay;
        AvailableDisplay = status.AvailableDisplay;
        LoadDisplay = status.LoadDisplay;

        // Auto-purge off the UI thread (same reason as PurgeAsync). _autoPurgeInFlight
        // guards against the 2s timer stacking a second purge on top of one still running.
        if (AutoPurgeEnabled && IsElevated && !_autoPurgeInFlight
            && ShouldAutoPurge(status.AvailableMb, ThresholdMb))
        {
            _autoPurgeInFlight = true;
            try
            {
                var avail = status.AvailableMb;
                var purged = await Task.Run(() => _service.TryPurgeStandbyList(out _)).ConfigureAwait(true);
                if (purged)
                {
                    Log.Information("Auto-purged standby list (available {Avail:F0} MB < {Threshold:F0} MB)", avail, ThresholdMb);
                    StatusMessage = $"Auto-purged — available RAM was below {ThresholdMb:F0} MB.";
                    Refresh();
                }
            }
            finally { _autoPurgeInFlight = false; }
        }
    }

    [RelayCommand]
    private async Task PurgeAsync()
    {
        if (!IsElevated)
        {
            StatusMessage = "Purging the standby list requires administrator rights.";
            return;
        }

        // The native standby-list purge (NtSetSystemInformation) can block for a noticeable
        // time on a large cache, so run it off the UI thread to keep the window responsive —
        // mirrors PerformanceViewModel.TrimRamAsync. ConfigureAwait(true) resumes on the UI
        // thread so the status/Refresh updates marshal correctly.
        StatusMessage = "Purging standby list…";
        var (ok, error) = await Task.Run(() =>
        {
            var success = _service.TryPurgeStandbyList(out var err);
            return (success, err);
        }).ConfigureAwait(true);

        if (ok)
        {
            Log.Information("User purged standby list");
            StatusMessage = "Standby list purged — cached memory released to the free list.";
            Refresh();
        }
        else
        {
            StatusMessage = error;
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Stop();
        base.Dispose(disposing);
    }
}
