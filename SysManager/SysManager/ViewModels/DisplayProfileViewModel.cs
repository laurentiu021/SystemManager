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
    private readonly DisplayProfileService _service;
    private DispatcherTimer? _revertTimer;
    private DisplayMode? _previousMode;
    // The display the pending revert belongs to, captured at Apply time. The user can
    // switch SelectedDisplay during the 15 s countdown, so revert must target THIS
    // device — not whatever is selected when the timer fires — or it would apply
    // display A's old mode to display B.
    private DisplayDevice? _previousDevice;
    private const int RevertSeconds = 15;

    public BulkObservableCollection<DisplayDevice> Displays { get; } = new();
    public BulkObservableCollection<DisplayMode> Modes { get; } = new();

    [ObservableProperty] private DisplayDevice? _selectedDisplay;
    [ObservableProperty] private DisplayMode? _selectedMode;
    [ObservableProperty] private DisplayMode? _currentMode;
    [ObservableProperty] private bool _isSupported = true;

    // Pending-confirmation state (the auto-revert window).
    [ObservableProperty] private bool _isAwaitingConfirm;
    [ObservableProperty] private int _revertCountdown;

    public DisplayProfileViewModel(DisplayProfileService service)
    {
        _service = service;
        StatusMessage = "Reading displays…";
        InitializeAsync(LoadDisplaysAsync);
    }

    private async Task LoadDisplaysAsync()
    {
        var displays = await Task.Run(_service.GetDisplays).ConfigureAwait(true);
        Displays.ReplaceWith(displays);
        IsSupported = displays.Count > 0;
        if (!IsSupported)
        {
            StatusMessage = "No active displays were found.";
            return;
        }
        SelectedDisplay = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        StatusMessage = "Pick a resolution and refresh rate, then apply.";
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
        var modes = await Task.Run(() => _service.GetSupportedModes(deviceName)).ConfigureAwait(true);
        var current = await Task.Run(() => _service.GetCurrentMode(deviceName)).ConfigureAwait(true);
        // Rapid display switches launch overlapping loads; if the selection moved on while
        // this one was running, drop its results so a slow earlier load can't overwrite
        // the newer display's modes (last-writer-wins).
        if (SelectedDisplay is null || SelectedDisplay.DeviceName != deviceName) return;
        Modes.ReplaceWith(modes);
        CurrentMode = current;
        SelectedMode = modes.FirstOrDefault(m =>
            CurrentMode is not null && m.Width == CurrentMode.Width &&
            m.Height == CurrentMode.Height && m.RefreshHz == CurrentMode.RefreshHz);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModeChanged(DisplayMode? value) => ApplyCommand.NotifyCanExecuteChanged();

    private bool CanApply => IsSupported && !IsAwaitingConfirm && SelectedDisplay is not null
        && SelectedMode is not null && !SelectedMode.Equals(CurrentMode);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var display = SelectedDisplay;
        var mode = SelectedMode;
        if (display is null || mode is null) return;

        // ChangeDisplaySettingsEx can block while the driver re-trains the panel, and
        // GetCurrentMode is a P/Invoke read — run them off the UI thread so the window
        // (and the auto-revert DispatcherTimer) stays responsive during the switch.
        // Capture the device alongside its previous mode so a revert targets THIS
        // display even if the user changes the selection during the countdown.
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
        CurrentMode = await Task.Run(() => _service.GetCurrentMode(display.DeviceName)).ConfigureAwait(true);

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
        StatusMessage = $"Display set to {CurrentMode?.Display}.";
        ApplyCommand.NotifyCanExecuteChanged();
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
        _revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _revertTimer.Tick += OnRevertTick;
        _revertTimer.Start();
    }

    private void OnRevertTick(object? sender, EventArgs e)
    {
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
        IsAwaitingConfirm = false;

        // Revert the device the change was APPLIED to (captured at Apply time), not the
        // one currently selected — the user may have switched the picker during the
        // countdown, and reverting the wrong display would corrupt its mode.
        var device = _previousDevice;
        var prev = _previousMode;
        bool revertFailed = false;
        if (device is not null && prev is not null)
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
        _previousMode = null;
        _previousDevice = null;
        StatusMessage = revertFailed
            ? "Couldn't restore the previous display mode — if the screen looks wrong, press Esc or use Windows Settings ▸ System ▸ Display to reset it."
            : message;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void StopRevertTimer()
    {
        if (_revertTimer is null) return;
        _revertTimer.Stop();
        _revertTimer.Tick -= OnRevertTick;
        _revertTimer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopRevertTimer();
        base.Dispose(disposing);
    }
}
