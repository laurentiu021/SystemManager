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
            _timer.Tick += (_, _) => EvaluateSchedule();
            _timer.Start();
            EvaluateSchedule(); // apply immediately so the theme is correct on open
        }
    }

    [RelayCommand]
    private void SwitchToDark() => ApplyTheme(true, "Switched to dark.");

    [RelayCommand]
    private void SwitchToLight() => ApplyTheme(false, "Switched to light.");

    private void ApplyTheme(bool dark, string okMessage)
    {
        if (_service.SetTheme(dark, ApplyToSystem))
        {
            IsDarkNow = dark;
            StatusMessage = okMessage;
        }
        else
        {
            StatusMessage = "Couldn't change the Windows theme.";
        }
    }

    /// <summary>Apply the scheduled theme if the schedule is on and the current theme differs.</summary>
    private void EvaluateSchedule()
    {
        // Suppress the theme side effect during LoadFromSchedule: the [ObservableProperty]
        // setters fire one at a time, so the ScheduleEnabled setter would run this while
        // DarkStart/LightStart/ApplyToSystem still hold their defaults — writing the WRONG
        // (and possibly system-wide) theme against the user's saved preference. The ctor
        // runs one authoritative EvaluateSchedule() after load completes and IsDarkNow is
        // set, so nothing is lost. _suppressSave already gates SaveSchedule for the same reason.
        if (_suppressSave) return;
        if (!ScheduleEnabled) return;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        bool wantDark = WindowsThemeService.ShouldBeDark(now, ParseTime(DarkStart, 19, 0), ParseTime(LightStart, 7, 0));
        if (wantDark == IsDarkNow) return;

        if (_service.SetTheme(wantDark, ApplyToSystem))
        {
            IsDarkNow = wantDark;
            StatusMessage = $"Schedule applied {(wantDark ? "dark" : "light")} theme.";
            Log.Information("Dark-mode schedule applied {Mode}", wantDark ? "dark" : "light");
        }
    }

    partial void OnScheduleEnabledChanged(bool value) { SaveSchedule(); EvaluateSchedule(); }
    partial void OnDarkStartChanged(string value) { SaveSchedule(); EvaluateSchedule(); }
    partial void OnLightStartChanged(string value) { SaveSchedule(); EvaluateSchedule(); }
    partial void OnApplyToSystemChanged(bool value) => SaveSchedule();

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
        _service.SaveSchedule(new DarkModeSchedule
        {
            Enabled = ScheduleEnabled,
            DarkStart = DarkStart,
            LightStart = LightStart,
            ApplyToSystem = ApplyToSystem,
        });
    }

    private static TimeOnly ParseTime(string value, int h, int m)
        => TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(h, m);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Stop();
        base.Dispose(disposing);
    }
}
