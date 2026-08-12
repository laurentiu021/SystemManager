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
    private readonly StandbyPreferenceService _preferences;
    private readonly DispatcherTimer? _timer;
    // Suppresses saving while the constructor applies the loaded values, so restoring a
    // preference does not immediately rewrite the same file.
    private bool _loadingPreferences;

    [ObservableProperty] private string _totalDisplay = "—";
    [ObservableProperty] private string _availableDisplay = "—";
    [ObservableProperty] private string _loadDisplay = "—";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _autoPurgeEnabled;
    [ObservableProperty] private double _thresholdMb = StandbyPreferenceService.DefaultThresholdMb;

    /// <summary>
    /// True while this tab is the visible one. Set by <c>MainWindowViewModel.SetActive</c>, like the
    /// other polling tabs.
    /// </summary>
    /// <remarks>
    /// The poll used to start in the constructor and never stop, so opening this tab ONCE left a
    /// 2-second dispatcher tick running for the rest of the session — behind another tab, minimised,
    /// and closed to the tray. See <see cref="ShouldPoll"/> for why visibility alone is not the
    /// condition.
    /// </remarks>
    [ObservableProperty] private bool _isActive;

    public StandbyMemoryViewModel(StandbyMemoryService service, StandbyPreferenceService? preferences = null)
    {
        _service = service;
        _preferences = preferences ?? new StandbyPreferenceService();
        IsElevated = AdminHelper.IsElevated();

        // Auto-purge is set-and-forget, so losing it on restart made it effectively unusable:
        // the user armed it, closed the app, and it silently reverted to off at the default
        // threshold. Restore before the first Refresh so the very first tick already honours it.
        _loadingPreferences = true;
        var saved = _preferences.Load();
        AutoPurgeEnabled = saved.AutoPurgeEnabled;
        ThresholdMb = saved.ThresholdMb;
        _loadingPreferences = false;

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
            // NOT started here. The tab is constructed on first open, and MainWindowViewModel sets
            // IsActive immediately after, which starts it — so the visible case is unaffected while a
            // hidden tab no longer polls forever.
            SyncTimer();
        }
    }

    /// <summary>Pure: should auto-purge fire? True when available RAM is below the threshold.</summary>
    public static bool ShouldAutoPurge(double availableMb, double thresholdMb)
        => thresholdMb > 0 && availableMb > 0 && availableMb < thresholdMb;

    /// <summary>
    /// Pure: should the 2-second poll be running? True while the tab is visible, OR while auto-purge is
    /// armed and could actually act.
    /// </summary>
    /// <remarks>
    /// Visibility alone is the WRONG condition here, unlike the other polling tabs. Auto-purge is
    /// deliberately set-and-forget — the user arms it, navigates away, and expects it to keep watching
    /// free memory — so gating purely on IsActive would silently turn the feature off the moment the tab
    /// lost focus, which is a worse bug than the one being fixed.
    /// <para>Elevation is part of the condition because a purge needs administrator: without it the tick
    /// could never do anything but re-read memory into a hidden tab, which is exactly the waste this
    /// change removes.</para>
    /// </remarks>
    internal static bool ShouldPoll(bool isActive, bool autoPurgeEnabled, bool isElevated)
        => isActive || (autoPurgeEnabled && isElevated);

    /// <summary>Starts or stops the poll to match <see cref="ShouldPoll"/>.</summary>
    private void SyncTimer()
    {
        if (_timer is null) return;

        if (ShouldPoll(IsActive, AutoPurgeEnabled, IsElevated))
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    partial void OnIsActiveChanged(bool value) => SyncTimer();

    // Persist on change rather than on close: the app can be closed to the tray or killed, and a
    // setting the user visibly toggled should survive either.
    partial void OnAutoPurgeEnabledChanged(bool value)
    {
        SavePreferences();
        // Arming auto-purge has to be able to START the poll, and disarming it while the tab is hidden
        // has to STOP it — otherwise the "set and forget" path would either never watch or never stop.
        SyncTimer();
    }

    partial void OnThresholdMbChanged(double value) => SavePreferences();

    private void SavePreferences()
    {
        if (_loadingPreferences) return;
        _preferences.Save(new StandbyPreference(AutoPurgeEnabled, ThresholdMb));
    }

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
        try
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
        catch (Exception ex)
        {
            // Last-resort net: Tick is an async-void timer handler, so any escaping exception
            // (an unexpected memory-status or purge fault) would crash the whole process.
            // Swallow and log — a failed auto-refresh must never take the app down. Mirrors
            // TrayIconService.OnTimerTick.
            Log.Warning(ex, "Standby memory auto-refresh tick failed");
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
        // Only the MANUAL purge drives the progress bar. The 2 s auto-purge in Tick deliberately
        // does not: it would strobe the bar on and off in the background every couple of seconds.
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Purging standby list…";
        try
        {
            var (ok, error) = await Task.Run(() =>
            {
                var success = _service.TryPurgeStandbyList(out var err);
                return (success, err);
            }).ConfigureAwait(true);

            if (ok)
            {
                Log.Information("User purged standby list");
                ActivityLogService.Instance.Log("Standby cleaner", "Purged the standby memory list");
                StatusMessage = "Standby list purged — cached memory released to the free list.";
                Refresh();
            }
            else
            {
                StatusMessage = error;
            }
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
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
