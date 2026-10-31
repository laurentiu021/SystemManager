// SysManager · DisplayProfileViewModel
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
/// ViewModel for the Display Profiles tab. Lists displays and their supported
/// resolution/refresh modes, and switches to a selected mode for lower-latency gaming
/// or comfortable work. Applies for the session only, so a reboot reverts; on top of
/// that, a 15-second in-tab auto-revert restores the previous mode if the user doesn't
/// confirm — the safety net for a mode that blanks the panel. No admin required.
/// </summary>
public sealed partial class DisplayProfileViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshDisplaysCommand;

    /// <inheritdoc/>
    /// <remarks>
    /// Escape reverts a new mode that waits to be kept, as Windows' own "Keep these display settings?" does. The screen
    /// may be hard to read just then, and Escape is the one key that needs no aim (#2761).
    /// </remarks>
    protected internal override IRelayCommand? EscapeCancel => IsAwaitingConfirm ? RevertNowCommand : null;

    private readonly IDisplayProfileService _service;
    private readonly TimeProvider _clock;
    private ITimer? _revertTimer;
    private DisplayMode? _previousMode;
    // The display the pending revert belongs to, captured at Apply time. The user can
    // switch SelectedDisplay during the 15 s countdown, so revert must target THIS
    // device — not whatever is selected when the timer fires — or it would apply
    // display A's old mode to display B.
    private DisplayDevice? _previousDevice;
    // Incremented per mode load so only the newest one clears the busy flag (see LoadModesAsync).
    // UI-thread only — every mutation happens in a continuation marshalled back with ConfigureAwait(true).
    private int _modeLoadGeneration;
    // The same for reading the displays, which a burst of display changes can start several times over.
    private int _displayLoadGeneration;
    // A mode is being applied or reverted (see ChangingMode).
    private bool _changingMode;
    // Windows reported a display change while a mode was applied, reverted or waited to be kept. The displays are
    // read again once that is over, so a screen connected meanwhile is not missed.
    private bool _displaysChangedMeanwhile;
    // The mode Windows reported after the last apply, for Keep's message: the readout follows the selected display,
    // which can be another one by then.
    private DisplayMode? _appliedMode;
    // Held from Apply until the new mode is kept or the previous one is back, so closing SysManager meanwhile asks
    // first (see OperationCategory.Display).
    private OperationLockService.OperationHandle? _modeChangeLock;
    private const int RevertSeconds = 15;
    private static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);

    public BulkObservableCollection<DisplayDevice> Displays { get; } = new();
    public BulkObservableCollection<DisplayMode> Modes { get; } = new();

    [ObservableProperty] private DisplayDevice? _selectedDisplay;
    [ObservableProperty] private DisplayMode? _selectedMode;
    [ObservableProperty] private DisplayMode? _currentMode;
    [ObservableProperty] private bool _isSupported = true;

    // Pending-confirmation state (the auto-revert window).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDisplaysCommand))]
    private bool _isAwaitingConfirm;
    [ObservableProperty] private int _revertCountdown;

    public DisplayProfileViewModel(IDisplayProfileService service) : this(service, TimeProvider.System) { }

    /// <summary>
    /// The view model with the countdown's clock handed in, so a test lets the 15 seconds run out without waiting
    /// for them. A dispatcher timer counted them down, which ticks only in the running app, so no test reached the
    /// revert the countdown ends in (#2741).
    /// </summary>
    internal DisplayProfileViewModel(IDisplayProfileService service, TimeProvider clock)
    {
        _service = service;
        _clock = clock;
        StatusMessage = "Reading displays…";
        _service.DisplaysChanged += OnDisplaysChanged;
        InitializeAsync(() => LoadDisplaysAsync(announce: true));
    }

    /// <summary>
    /// Reads the displays, keeps the selected one while it is still connected, and reads its modes again.
    /// </summary>
    /// <param name="announce">
    /// Whether the status line then says what to do next. Not after a change Windows reported, which may be
    /// SysManager's own: the line can hold what an apply or a revert said, such as how to recover when a revert failed.
    /// </param>
    private async Task LoadDisplaysAsync(bool announce)
    {
        int generation = ++_displayLoadGeneration;
        IReadOnlyList<DisplayDevice> displays;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            displays = await Task.Run(_service.GetDisplays).ConfigureAwait(true);
        }
        finally
        {
            if (generation == _displayLoadGeneration) { IsBusy = false; IsProgressIndeterminate = false; }
        }

        // A newer read started while this one ran, and its list is the one to show: this one's could still hold a
        // screen that has since been removed.
        if (generation != _displayLoadGeneration) return;

        // Taken before the picker's items are replaced, which can clear its selection.
        var selectedName = SelectedDisplay?.DeviceName;
        Displays.ReplaceWith(displays);
        IsSupported = displays.Count > 0;
        if (!IsSupported)
        {
            // A read can find none where the last one found some, and the modes shown were that display's.
            SelectedDisplay = null;
            Modes.ReplaceWith([]);
            CurrentMode = null;
            if (announce) StatusMessage = "No active displays were found.";
            return;
        }

        var keep = displays.FirstOrDefault(d => d.DeviceName == selectedName)
                   ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        // Assigned after the block above, not inside it: this setter starts the mode load, which
        // owns IsBusy for its own span. Nested inside, our finally would clear the flag while that
        // load was still running and the bar would vanish mid-work. The same display read again is
        // an equal record, so the setter would not start that load, yet its modes and its current
        // mode may have changed with it.
        if (Equals(keep, SelectedDisplay)) InitializeAsync(() => LoadModesAsync(keep.DeviceName));
        else SelectedDisplay = keep;
        if (announce) StatusMessage = "Pick a resolution and refresh rate, then apply.";
    }

    /// <summary>Reads the displays again, from the Refresh button or F5.</summary>
    /// <remarks>
    /// The displays were read once, when the tab was first built, and the view model lives as long as SysManager,
    /// which keeps running in the notification area, so a monitor or TV connected later never appeared and could not
    /// be set until SysManager restarted (#2886). Not while a mode is applied or reverted, or waits to be kept: what
    /// the tab shows then is the change being made, and the displays are read once it is over.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefreshDisplays))]
    private Task RefreshDisplaysAsync() => LoadDisplaysAsync(announce: true);

    private bool CanRefreshDisplays => !_changingMode && !IsAwaitingConfirm;

    /// <summary>Whether a mode is being applied or reverted. Windows reports that as a display change too.</summary>
    private bool ChangingMode
    {
        get => _changingMode;
        set
        {
            _changingMode = value;
            RefreshDisplaysCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    // Windows reported a display change: a screen connected or removed, or a mode set, SysManager's own included. It
    // comes in on whichever thread Windows' notification arrived on, so it is marshalled.
    private void OnDisplaysChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (IsDisposed) return;
        if (!CanRefreshDisplays)
        {
            _displaysChangedMeanwhile = true;
            return;
        }
        InitializeAsync(() => LoadDisplaysAsync(announce: false));
    });

    /// <summary>Reads the displays again if Windows reported a change while a mode was applied, reverted or kept.</summary>
    private void ReadDisplaysChangedMeanwhile()
    {
        if (!_displaysChangedMeanwhile) return;
        _displaysChangedMeanwhile = false;
        InitializeAsync(() => LoadDisplaysAsync(announce: false));
    }

    partial void OnSelectedDisplayChanged(DisplayDevice? value)
    {
        if (value is null) return;
        // Enumerating modes loops EnumDisplaySettings over every supported mode and
        // reads the current mode — both are blocking P/Invoke, so run them off the UI
        // thread and marshal the results back (ConfigureAwait(true)). Launched through
        // the guarded helper so an unexpected failure is logged, not unobserved.
        InitializeAsync(() => LoadModesAsync(value.DeviceName));
    }

    private async Task LoadModesAsync(string deviceName)
    {
        // Rapid display switches launch overlapping loads, so the busy flag is owned by the NEWEST
        // one: an older load finishing must not clear the bar while the newer one is still working.
        int generation = ++_modeLoadGeneration;
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var modes = await Task.Run(() => _service.GetSupportedModes(deviceName)).ConfigureAwait(true);
            var current = await Task.Run(() => _service.GetCurrentMode(deviceName)).ConfigureAwait(true);
            // If the selection moved on while this one was running, drop its results so a slow
            // earlier load can't overwrite the newer display's modes (last-writer-wins). The same
            // for a newer load of this display, which reading the displays again starts.
            if (generation != _modeLoadGeneration || SelectedDisplay is null || SelectedDisplay.DeviceName != deviceName) return;
            Modes.ReplaceWith(modes);
            CurrentMode = current;
            SelectedMode = modes.FirstOrDefault(m =>
                CurrentMode is not null && m.Width == CurrentMode.Width &&
                m.Height == CurrentMode.Height && m.RefreshHz == CurrentMode.RefreshHz);
            ApplyCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            if (generation == _modeLoadGeneration) { IsBusy = false; IsProgressIndeterminate = false; }
        }
    }

    partial void OnSelectedModeChanged(DisplayMode? value) => ApplyCommand.NotifyCanExecuteChanged();

    // Not while a revert re-trains the panel either: it ends the countdown first, and an apply started then recorded
    // its own previous mode over the one the revert was putting back (#2761).
    private bool CanApply => IsSupported && !IsAwaitingConfirm && !_changingMode && SelectedDisplay is not null
        && SelectedMode is not null && !SelectedMode.Equals(CurrentMode);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var display = SelectedDisplay;
        var mode = SelectedMode;
        if (display is null || mode is null) return;

        // ChangeDisplaySettingsEx can block while the driver re-trains the panel, and
        // GetCurrentMode is a P/Invoke read — run them off the UI thread so the window
        // (and the auto-revert countdown) stays responsive during the switch.
        // Capture the device alongside its previous mode so a revert targets THIS
        // display even if the user changes the selection during the countdown.
        // This is the slowest thing the tab does — a panel re-train can take seconds — so it is
        // exactly what the progress bar exists for. The flag is cleared before the countdown starts:
        // waiting 15 s for the user to confirm is not the app being busy.
        IsBusy = true;
        IsProgressIndeterminate = true;
        ChangingMode = true;
        _modeChangeLock ??= OperationLockService.Instance.TryAcquire(OperationCategory.Display, "Display mode change");
        var changed = false;
        try
        {
            _previousDevice = display;
            _previousMode = await Task.Run(() => _service.GetCurrentMode(display.DeviceName)).ConfigureAwait(true);

            var (ok, error) = await Task.Run(() =>
            {
                bool applied = _service.TryApplyMode(display.DeviceName, mode.Width, mode.Height, mode.RefreshHz, out string err);
                return (applied, err);
            }).ConfigureAwait(true);
            if (!ok)
            {
                StatusMessage = error;
                return;
            }

            Log.Information("Applied display mode {Mode} to {Device}", mode.Display, display.DeviceName);
            var now = await Task.Run(() => _service.GetCurrentMode(display.DeviceName)).ConfigureAwait(true);
            _appliedMode = now ?? mode;

            // Only for the display still selected, as the revert does: the picker can move while the panel re-trains,
            // and the readout then belongs to the display picked since (#2761).
            if (ReferenceEquals(display, SelectedDisplay)) CurrentMode = now;
            changed = true;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ChangingMode = false;

            // A change Windows refused starts no countdown, so a display change it reported meanwhile is read now.
            if (!changed)
            {
                EndModeChange();
                ReadDisplaysChangedMeanwhile();
            }
        }

        // Begin the auto-revert window — the user must confirm to keep the change.
        BeginRevertCountdown();
    }

    [RelayCommand]
    private void KeepSettings()
    {
        StopRevertTimer();
        IsAwaitingConfirm = false;
        _previousMode = null;
        _previousDevice = null;
        StatusMessage = $"Display set to {(_appliedMode ?? CurrentMode)?.Display}.";
        EndModeChange();
        ApplyCommand.NotifyCanExecuteChanged();
        ReadDisplaysChangedMeanwhile();
    }

    /// <summary>Releases what a mode change holds once it is kept, reverted or refused.</summary>
    private void EndModeChange()
    {
        _appliedMode = null;
        _modeChangeLock?.Dispose();
        _modeChangeLock = null;
    }

    [RelayCommand]
    private Task RevertNowAsync() => RevertToPreviousAsync("Reverted to the previous display mode.");

    private void BeginRevertCountdown()
    {
        IsAwaitingConfirm = true;
        RevertCountdown = RevertSeconds;
        StatusMessage = $"Keep these settings? Reverting in {RevertCountdown}s…";
        ApplyCommand.NotifyCanExecuteChanged();

        StopRevertTimer();

        // Each second on the UI thread, at the priority the dispatcher timer that did this had. A tick names the timer
        // that sent it, so one already on its way when that timer was stopped, or replaced by the next Apply's, is
        // dropped, as a stopped dispatcher timer dropped its own. With no Application, in unit tests, a tick runs
        // where the injected clock fires it.
        ITimer? timer = null;
        timer = _clock.CreateTimer(
            _ => UiThread.Post(() => OnRevertTick(timer), DispatcherPriority.Background), null, CountdownTick, CountdownTick);
        _revertTimer = timer;
    }

    private void OnRevertTick(ITimer? from)
    {
        // From a timer Keep, Revert now or closing stopped, or the next Apply replaced.
        if (from is null || !ReferenceEquals(from, _revertTimer)) return;

        RevertCountdown--;
        if (RevertCountdown > 0)
        {
            StatusMessage = $"Keep these settings? Reverting in {RevertCountdown}s…";
            return;
        }
        // Launch the (async) revert through the guarded helper — the timer callback is
        // void, so an awaited failure would otherwise be unobserved.
        InitializeAsync(() => RevertToPreviousAsync("No confirmation — reverted to the previous display mode."));
    }

    private async Task RevertToPreviousAsync(string message)
    {
        StopRevertTimer();
        // Before the countdown ends, so the displays are not read again in between: the revert changes a mode too.
        ChangingMode = true;
        IsAwaitingConfirm = false;

        // Revert the device the change was APPLIED to (captured at Apply time), not the
        // one currently selected — the user may have switched the picker during the
        // countdown, and reverting the wrong display would corrupt its mode.
        var device = _previousDevice;
        var prev = _previousMode;
        bool revertFailed = false;
        if (device is not null && prev is not null)
        {
            // Reverting re-trains the panel exactly like applying does, so it gets the same
            // feedback — and here it matters more: the screen may be blank while this runs.
            IsBusy = true;
            IsProgressIndeterminate = true;
            try
            {
                // Revert is the only in-app safety net for a mode that blanked the panel, so its
                // success matters — don't discard it. If the driver rejects the previous mode,
                // tell the user exactly how to recover via Windows rather than claiming success.
                var (reverted, revertError) = await Task.Run(() =>
                {
                    var ok = _service.TryApplyMode(device.DeviceName, prev.Width, prev.Height, prev.RefreshHz, out var err);
                    return (ok, err);
                }).ConfigureAwait(true);
                revertFailed = !reverted;
                if (revertFailed)
                    Log.Warning("Display auto-revert failed for {Device}: {Error}", device.DeviceName, revertError);

                // Only refresh CurrentMode if the reverted device is still the selected one,
                // so we don't overwrite the display the user has since switched to.
                if (ReferenceEquals(device, SelectedDisplay))
                    CurrentMode = await Task.Run(() => _service.GetCurrentMode(device.DeviceName)).ConfigureAwait(true);
            }
            finally { IsBusy = false; IsProgressIndeterminate = false; ChangingMode = false; }
        }
        else ChangingMode = false;
        _previousMode = null;
        _previousDevice = null;

        // Not "press Esc": nothing is left for Escape to revert once this has run. A restart does put the mode back,
        // since the change was made for this session only.
        StatusMessage = revertFailed
            ? "Couldn't restore the previous display mode. If the screen looks wrong, restart the PC, which puts it back, or use Windows Settings ▸ System ▸ Display."
            : message;
        EndModeChange();
        ApplyCommand.NotifyCanExecuteChanged();
        ReadDisplaysChangedMeanwhile();
    }

    /// <summary>
    /// Puts back the mode a change replaced when SysManager closes while that change waits to be kept.
    /// </summary>
    /// <remarks>
    /// The change lasts until the next mode change, sign-out or restart, and the countdown was its only way back. So
    /// exiting from the notification area, closing the window or restarting as administrator in those 15 seconds kept
    /// a mode nobody confirmed, the case the countdown exists for (#2761). Run while SysManager closes, so it waits for
    /// the panel, and a failure can only be logged.
    /// </remarks>
    private void PutBackAModeNotKept()
    {
        if (!IsAwaitingConfirm || _previousDevice is not { } device || _previousMode is not { } previous) return;

        if (_service.TryApplyMode(device.DeviceName, previous.Width, previous.Height, previous.RefreshHz, out var error))
            Log.Information("Put back display mode {Mode} on {Device} as SysManager closed", previous.Display, device.DeviceName);
        else
            Log.Warning("Could not put back display mode {Mode} on {Device} as SysManager closed: {Error}",
                        previous.Display, device.DeviceName, error);
    }

    private void StopRevertTimer()
    {
        _revertTimer?.Dispose();
        _revertTimer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopRevertTimer();
            PutBackAModeNotKept();
            EndModeChange();

            // Windows' display notification keeps its handlers in a static list, so one left there would keep this
            // view model alive, reading displays, for the rest of the process.
            _service.DisplaysChanged -= OnDisplaysChanged;
        }
        base.Dispose(disposing);
    }
}
