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
        var modes = _service.GetSupportedModes(value.DeviceName);
        Modes.ReplaceWith(modes);
        CurrentMode = _service.GetCurrentMode(value.DeviceName);
        SelectedMode = modes.FirstOrDefault(m =>
            CurrentMode is not null && m.Width == CurrentMode.Width &&
            m.Height == CurrentMode.Height && m.RefreshHz == CurrentMode.RefreshHz);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModeChanged(DisplayMode? value) => ApplyCommand.NotifyCanExecuteChanged();

    private bool CanApply => IsSupported && !IsAwaitingConfirm && SelectedDisplay is not null
        && SelectedMode is not null && !SelectedMode.Equals(CurrentMode);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var display = SelectedDisplay;
        var mode = SelectedMode;
        if (display is null || mode is null) return;

        _previousMode = _service.GetCurrentMode(display.DeviceName);

        bool ok = _service.TryApplyMode(display.DeviceName, mode.Width, mode.Height, mode.RefreshHz, out string error);
        if (!ok)
        {
            StatusMessage = error;
            return;
        }

        Log.Information("Applied display mode {Mode} to {Device}", mode.Display, display.DeviceName);
        CurrentMode = _service.GetCurrentMode(display.DeviceName);

        // Begin the auto-revert window — the user must confirm to keep the change.
        BeginRevertCountdown();
    }

    [RelayCommand]
    private void KeepSettings()
    {
        StopRevertTimer();
        IsAwaitingConfirm = false;
        _previousMode = null;
        StatusMessage = $"Display set to {CurrentMode?.Display}.";
        ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RevertNow() => RevertToPrevious("Reverted to the previous display mode.");

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
        RevertToPrevious("No confirmation — reverted to the previous display mode.");
    }

    private void RevertToPrevious(string message)
    {
        StopRevertTimer();
        IsAwaitingConfirm = false;

        var display = SelectedDisplay;
        if (display is not null && _previousMode is not null)
        {
            _service.TryApplyMode(display.DeviceName, _previousMode.Width, _previousMode.Height, _previousMode.RefreshHz, out _);
            CurrentMode = _service.GetCurrentMode(display.DeviceName);
        }
        _previousMode = null;
        StatusMessage = message;
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
