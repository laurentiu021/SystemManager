// SysManager · DarkModeViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer? _timer;
    private bool _suppressSave;

    /// <summary>Whether a theme switch is running, on a worker thread (see <see cref="SwitchAsync"/>).</summary>
    private bool _switching;

    /// <summary>
    /// Until when, on the wall clock, a switch made with Dark or Light holds against the schedule: the schedule's next
    /// change after it. Null while none holds (#2760).
    /// </summary>
    private DateTime? _heldUntil;

    // The times the schedule runs on: the last each box held that it can read.
    private string _darkStartInEffect = DarkModeSchedule.DefaultDarkStart;
    private string _lightStartInEffect = DarkModeSchedule.DefaultLightStart;

    [ObservableProperty] private bool _isDarkNow;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _darkStart = DarkModeSchedule.DefaultDarkStart;
    [ObservableProperty] private string _lightStart = DarkModeSchedule.DefaultLightStart;
    [ObservableProperty] private bool _applyToSystem = true;

    public DarkModeViewModel(IWindowsThemeService service) : this(service, TimeProvider.System) { }

    /// <summary>
    /// The view model with the clock the schedule reads handed in, so a test sets the time instead of waiting for it
    /// (#2760).
    /// </summary>
    internal DarkModeViewModel(IWindowsThemeService service, TimeProvider clock)
    {
        _service = service;
        _clock = clock;
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
            _timer.Tick += (_, _) => OnScheduleTick();
            _timer.Start();
            InitializeAsync(EvaluateScheduleAsync); // apply immediately so the theme is correct on open
        }
    }

    /// <summary>What the schedule's timer does each minute: looks at the schedule again.</summary>
    internal void OnScheduleTick() => InitializeAsync(EvaluateScheduleAsync);

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
            StatusMessage = ScheduleEnabled ? HoldAgainstTheSchedule(dark, okMessage) : okMessage;
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

    /// <summary>
    /// Holds the switch just made against the schedule until the schedule's next change, and says until when the theme
    /// stays so (#2760).
    /// </summary>
    /// <remarks>
    /// The schedule compares the theme it wants with the one SysManager last set, so a switch made here was undone at
    /// the next minute's check, while the same switch made in Windows Settings, which SysManager does not see, held
    /// until the schedule's next change. Now both hold until then.
    /// </remarks>
    private string HoldAgainstTheSchedule(bool dark, string okMessage)
    {
        var times = new DarkModeSchedule { DarkStart = DarkStart, LightStart = LightStart };
        _heldUntil = NextChange(_clock.GetLocalNow().DateTime, times.DarkStartTime, times.LightStartTime);

        // With the two times the same the schedule never wants dark, so light is simply where it stays.
        if (!dark && times.DarkStartTime == times.LightStartTime) return okMessage;
        return dark
            ? $"{okMessage} It stays so until the schedule turns light at {LightStart}."
            : $"{okMessage} It stays so until the schedule turns dark at {DarkStart}.";
    }

    /// <summary>The schedule's next change after <paramref name="now"/>: the first of its two times still to come.</summary>
    internal static DateTime NextChange(DateTime now, TimeOnly darkStart, TimeOnly lightStart)
    {
        var dark = NextAt(now, darkStart);
        var light = NextAt(now, lightStart);
        return dark < light ? dark : light;
    }

    private static DateTime NextAt(DateTime now, TimeOnly time)
    {
        var today = now.Date + time.ToTimeSpan();
        return today > now ? today : today.AddDays(1);
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
        var now = _clock.GetLocalNow().DateTime;
        // A switch made with Dark or Light holds until the schedule's next change (#2760).
        if (_heldUntil is { } until)
        {
            if (now < until) return;
            _heldUntil = null;
        }

        var times = new DarkModeSchedule { DarkStart = DarkStart, LightStart = LightStart };
        bool wantDark = WindowsThemeService.ShouldBeDark(TimeOnly.FromDateTime(now), times.DarkStartTime, times.LightStartTime);
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
    partial void OnApplyToSystemChanged(bool value) => SaveSchedule();

    partial void OnDarkStartChanged(string value)
    {
        var readable = Readable(value, _darkStartInEffect, dark: true);
        // Written as the schedule reads it, or put back: the set comes back here with a time it can read.
        if (readable != value) { DarkStart = readable; return; }
        if (readable == _darkStartInEffect) return;
        _darkStartInEffect = readable;
        ApplyThenSaveSchedule();
    }

    partial void OnLightStartChanged(string value)
    {
        var readable = Readable(value, _lightStartInEffect, dark: false);
        if (readable != value) { LightStart = readable; return; }
        if (readable == _lightStartInEffect) return;
        _lightStartInEffect = readable;
        ApplyThenSaveSchedule();
    }

    /// <summary>
    /// <paramref name="typed"/> written as the schedule reads it ("7:30" is "07:30"), or <paramref name="inEffect"/>
    /// when the schedule cannot read it, with the status line saying why the box went back.
    /// </summary>
    /// <remarks>
    /// A box took any text and saved it as typed, and the schedule then read what it could not as 19:00 or 07:00 without
    /// a word, while the box went on showing "19h" (#2760). A saved time it cannot read shows as the one it runs on,
    /// without a complaint as the tab opens.
    /// </remarks>
    private string Readable(string typed, string inEffect, bool dark)
    {
        if (DarkModeSchedule.TryReadTime(typed) is { } time)
            return time.ToString(DarkModeSchedule.TimeFormat, CultureInfo.InvariantCulture);
        if (!_suppressSave)
        {
            var why = string.IsNullOrWhiteSpace(typed)
                ? "A time is needed, so the schedule"
                : $"\"{typed.Trim()}\" is not a time the schedule can read, so it";
            StatusMessage = $"{why} still turns {(dark ? "dark" : "light")} at {inEffect}. "
                            + $"Type hours and minutes, as in {(dark ? "19:30" : "07:30")}.";
        }
        return inEffect;
    }

    // A change to the schedule is the newest word on the theme, so it ends a switch that was holding (#2760).
    private void ApplyThenSaveSchedule() => InitializeAsync(async () =>
    {
        _heldUntil = null;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Stop();
        base.Dispose(disposing);
    }
}
