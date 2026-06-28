// SysManager · TimerResolutionViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Timer Resolution tab. Shows the current Windows timer
/// resolution and lets the user request the finest available (≈0.5 ms) to reduce
/// input latency in games, or release it back toward the default. Fully reversible;
/// no admin required.
/// </summary>
public sealed partial class TimerResolutionViewModel : ViewModelBase
{
    private readonly ITimerResolutionService _service;

    [ObservableProperty] private string _currentDisplay = "—";
    [ObservableProperty] private string _finestDisplay = "—";
    [ObservableProperty] private string _coarsestDisplay = "—";
    [ObservableProperty] private bool _isHighResolution;
    [ObservableProperty] private bool _isSupported = true;

    public TimerResolutionViewModel(ITimerResolutionService service)
    {
        _service = service;
        StatusMessage = "Reading timer resolution…";
        InitializeAsync(RefreshAsync);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var status = await Task.Run(_service.Query).ConfigureAwait(true);
        Apply(status);
        StatusMessage = IsSupported
            ? (IsHighResolution
                ? "High-resolution timer is active."
                : "Timer is at the Windows default.")
            : "Timer resolution information is not available on this system.";
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task EnableAsync()
    {
        var status = await Task.Run(_service.Enable).ConfigureAwait(true);
        Apply(status);
        StatusMessage = IsHighResolution
            ? $"High-resolution timer enabled ({status.CurrentDisplay})."
            : "Could not raise the timer resolution on this system.";
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync()
    {
        var status = await Task.Run(_service.Disable).ConfigureAwait(true);
        Apply(status);
        StatusMessage = "Released the timer request — back to the Windows default.";
    }

    private bool CanEnable => IsSupported && !IsHighResolution;
    private bool CanDisable => IsSupported && IsHighResolution;

    private void Apply(TimerResolutionStatus status)
    {
        IsSupported = status.CoarsestHundredNs != 0;
        CurrentDisplay = IsSupported ? status.CurrentDisplay : "—";
        FinestDisplay = IsSupported ? status.FinestDisplay : "—";
        CoarsestDisplay = IsSupported ? status.CoarsestDisplay : "—";
        IsHighResolution = IsSupported && status.IsHighResolution;
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
    }
}
