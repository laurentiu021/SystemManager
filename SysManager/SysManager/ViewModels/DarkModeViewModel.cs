// SysManager · DarkModeViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Dark Mode Scheduler tab. Switches the Windows theme now, and runs a
/// fixed-time schedule (dark at X, light at Y) while the app is open. The schedule is not
/// a Windows service — it only applies while SysManager (or its tray) is running.
/// </summary>
public sealed partial class DarkModeViewModel : ViewModelBase
{
    private readonly IWindowsThemeService _service;
    private readonly DispatcherTimer? _timer;
    private bool _suppressSave;

    /// <summary>Whether a theme switch is running, on a worker thread (see <see cref="SwitchAsync"/>).</summary>
    private bool _switching;

    [ObservableProperty] private bool _isDarkNow;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _darkStart = "19:00";
    [ObservableProperty] private string _lightStart = "07:00";
    [ObservableProperty] private bool _applyToSystem = true;

    public DarkModeViewModel(IWindowsThemeService service)
    {
        _service = service;
        LoadFromSchedule();
        IsDarkNow = _service.GetCurrentTheme() == WindowsTheme.Dark;
        StatusMessage = ScheduleEnabled
            ? "Schedule is on — the theme follows your set times while SysManager is running."
            : "Switch the Windows theme, or set a schedule.";

        // Re-entrancy-safe poll (60s), mirroring the tray timer; skipped in tests (no dispatcher).
        if (System.Windows.Application.Current is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(60),
            };
            _timer.Tick += (_, _) => InitializeAsync(EvaluateScheduleAsync);
            _timer.Start();
            InitializeAsync(EvaluateScheduleAsync); // apply immediately so the theme is correct on open
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task SwitchToDarkAsync() => ApplyThemeAsync(true, "Switched to dark.");

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task SwitchToLightAsync() => ApplyThemeAsync(false, "Switched to light.");

    private bool CanSwitch() => !_switching;

    private async Task ApplyThemeAsync(bool dark, string okMessage)
    {
        if (await SwitchAsync(dark))
        {
            IsDarkNow = dark;
            StatusMessage = okMessage;
        }
        else
        {
            StatusMessage = "Couldn't change the Windows theme.";
        }
    }

    /// <summary>
    /// Switches the Windows theme on a worker thread. Both buttons are off until it is done, and the schedule does not
    /// start another meanwhile, so two switches never race to write the theme.
    /// </summary>
    /// <remarks>
    /// The switch ends by telling every open window that the theme changed, and Windows gives each window up to five
    /// seconds to answer. Run on the window's thread, one program that was busy but not yet "not responding" froze
    /// SysManager for that long, and several added up (#2750).
    /// </remarks>
    private async Task<bool> SwitchAsync(bool dark)
    {
        var includeSystem = ApplyToSystem;
        SetSwitching(true);
        try
        {
            return await Task.Run(() => _service.SetTheme(dark, includeSystem)).ConfigureAwait(true);
        }
        finally
        {
            SetSwitching(false);
        }
    }

    private void SetSwitching(bool value)
    {
        _switching = value;
        SwitchToDarkCommand.NotifyCanExecuteChanged();
        SwitchToLightCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Apply the scheduled theme if the schedule is on and the current theme differs.</summary>
    private async Task EvaluateScheduleAsync()
    {
        // Suppress the theme side effect during LoadFromSchedule: the [ObservableProperty]
        // setters fire one at a time, so the ScheduleEnabled setter would run this while
        // DarkStart/LightStart/ApplyToSystem still hold their defaults — writing the WRONG
        // (and possibly system-wide) theme against the user's saved preference. The ctor
        // runs one authoritative EvaluateScheduleAsync() after load completes and IsDarkNow is
        // set, so nothing is lost. _suppressSave already gates SaveSchedule for the same reason.
        if (_suppressSave) return;
        if (!ScheduleEnabled) return;
        // A switch is already running: it decides the theme for now, and the next minute's check starts from it.
        if (_switching) return;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        bool wantDark = WindowsThemeService.ShouldBeDark(now, ParseTime(DarkStart, 19, 0), ParseTime(LightStart, 7, 0));
        if (wantDark == IsDarkNow) return;

        if (await SwitchAsync(wantDark))
        {
            IsDarkNow = wantDark;
            StatusMessage = $"Schedule applied {(wantDark ? "dark" : "light")} theme.";
            Log.Information("Dark-mode schedule applied {Mode}", wantDark ? "dark" : "light");
        }
    }

    // Saved after the schedule is evaluated, so a change that could not be saved is the last word on the status
    // line: switching the theme would otherwise replace it with "Schedule applied" (#2521).
    partial void OnScheduleEnabledChanged(bool value) => ApplyThenSaveSchedule();
    partial void OnDarkStartChanged(string value) => ApplyThenSaveSchedule();
    partial void OnLightStartChanged(string value) => ApplyThenSaveSchedule();
    partial void OnApplyToSystemChanged(bool value) => SaveSchedule();

    private void ApplyThenSaveSchedule() => InitializeAsync(async () =>
    {
        await EvaluateScheduleAsync();
        SaveSchedule();
    });

    private void LoadFromSchedule()
    {
        _suppressSave = true;
        var s = _service.LoadSchedule();
        ScheduleEnabled = s.Enabled;
        DarkStart = s.DarkStart;
        LightStart = s.LightStart;
        ApplyToSystem = s.ApplyToSystem;
        _suppressSave = false;
    }

    private void SaveSchedule()
    {
        if (_suppressSave) return;
        var saved = _service.SaveSchedule(new DarkModeSchedule
        {
            Enabled = ScheduleEnabled,
            DarkStart = DarkStart,
            LightStart = LightStart,
            ApplyToSystem = ApplyToSystem,
        });
        if (!saved) StatusMessage = ChangeNotSavedStatus;
    }

    private static TimeOnly ParseTime(string value, int h, int m)
        => TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(h, m);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Stop();
        base.Dispose(disposing);
    }
}
