// SysManager · ScheduledMaintenanceViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Scheduled Maintenance tab. Lets the user register a single recurring
/// Windows task that runs SysManager headless (temp cleanup or standby trim) on a daily or
/// weekly schedule, and shows its status / last run. Creating or removing the task is
/// confirmed first; only SysManager's own task is ever touched (via
/// <see cref="MaintenanceSchedulerService"/>).
/// </summary>
public sealed partial class ScheduledMaintenanceViewModel : ViewModelBase
{
    private readonly MaintenanceSchedulerService _service;

    public IReadOnlyList<MaintenanceAction> Actions { get; } = [MaintenanceAction.Cleanup, MaintenanceAction.TrimRam];
    public IReadOnlyList<MaintenanceFrequency> Frequencies { get; } = [MaintenanceFrequency.Daily, MaintenanceFrequency.Weekly];
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = [.. Enumerable.Range(0, 24)];
    public IReadOnlyList<int> Minutes { get; } = [0, 15, 30, 45];

    [ObservableProperty] private MaintenanceAction _selectedAction = MaintenanceAction.Cleanup;
    [ObservableProperty] private MaintenanceFrequency _selectedFrequency = MaintenanceFrequency.Weekly;
    [ObservableProperty] private DayOfWeek _selectedDay = DayOfWeek.Sunday;
    [ObservableProperty] private int _selectedHour = 3;
    [ObservableProperty] private int _selectedMinute = 0;
    [ObservableProperty] private bool _isWeekly = true;

    [ObservableProperty] private bool _isScheduled;
    [ObservableProperty] private string _currentSummary = "";
    [ObservableProperty] private string _lastRun = "";
    [ObservableProperty] private string _nextRun = "";
    [ObservableProperty] private string _lastResult = "";

    public ScheduledMaintenanceViewModel(MaintenanceSchedulerService service)
    {
        _service = service;
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    partial void OnSelectedFrequencyChanged(MaintenanceFrequency value)
        => IsWeekly = value == MaintenanceFrequency.Weekly;

    /// <summary>Gate for the async commands so a second click can't start an overlapping
    /// Save/Remove/Refresh while one is in flight (which would race IsBusy + the read-back).</summary>
    private bool NotBusy => !IsBusy;

    // IsBusy lives in ViewModelBase, so the [ObservableProperty] partial hook isn't generated
    // here — observe it via PropertyChanged to re-evaluate the gated commands' CanExecute.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        RefreshCommand.NotifyCanExecuteChanged();
        SaveScheduleCommand.NotifyCanExecuteChanged();
        RemoveScheduleCommand.NotifyCanExecuteChanged();
    }

    private MaintenanceSchedule BuildSchedule() =>
        new(SelectedAction, SelectedFrequency, SelectedHour, SelectedMinute, SelectedDay);

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadStatusAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Reads the task status and updates the bound state. Does NOT touch <see cref="IsBusy"/>,
    /// so Save/Remove can call it without prematurely clearing their own busy flag (which would
    /// re-enable the commands mid-operation).
    /// </summary>
    private async Task LoadStatusAsync()
    {
        var status = await _service.GetStatusAsync();
        IsScheduled = status.Exists;
        if (status.Exists)
        {
            CurrentSummary = $"Maintenance is scheduled (state: {status.State}).";
            LastRun = status.LastRun is { } lr ? lr.ToString("yyyy-MM-dd HH:mm") : "—";
            NextRun = status.NextRun is { } nr ? nr.ToString("yyyy-MM-dd HH:mm") : "—";
            LastResult = status.LastResultDescription ?? "";
            StatusMessage = "A maintenance task is registered. You can update or remove it below.";
        }
        else
        {
            CurrentSummary = "No maintenance is scheduled yet.";
            LastRun = NextRun = LastResult = "";
            StatusMessage = "Pick an action and time, then Save schedule to automate it.";
        }
        RemoveScheduleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveScheduleAsync()
    {
        var schedule = BuildSchedule();
        if (!DialogService.Instance.Confirm(
                $"Schedule \"{schedule.ActionLabel}\" to run automatically?\n\n{schedule.Summary}\n\n" +
                "This creates a Windows scheduled task that launches SysManager in the background.",
                "Schedule Maintenance — Confirm"))
            return;

        IsBusy = true;
        try
        {
            bool ok = await _service.RegisterAsync(schedule);
            await LoadStatusAsync(); // refresh state first, then set the outcome message so it isn't overwritten
            if (ok)
            {
                ActivityLogService.Instance.Log("Scheduled Maintenance", $"{schedule.ActionLabel} — {schedule.Summary}");
                StatusMessage = $"Scheduled: {schedule.ActionLabel.ToLowerInvariant()}, {schedule.Summary.ToLowerInvariant()}.";
                Log.Information("Maintenance scheduled: {Action} {Summary}", schedule.ActionLabel, schedule.Summary);
            }
            else
            {
                StatusMessage = "Could not register the scheduled task. Check the log for details.";
            }
        }
        finally { IsBusy = false; }
    }

    private bool CanRemove() => IsScheduled && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveScheduleAsync()
    {
        if (!DialogService.Instance.Confirm(
                "Remove the scheduled maintenance task?\n\nSysManager will no longer run maintenance automatically.",
                "Remove Schedule — Confirm"))
            return;

        IsBusy = true;
        try
        {
            bool removed = await _service.RemoveAsync();
            if (removed) ActivityLogService.Instance.Log("Scheduled Maintenance", "Removed the maintenance schedule");
            await LoadStatusAsync(); // refresh state first, then set the outcome message
            StatusMessage = removed ? "Scheduled maintenance removed." : "Could not remove the task. Check the log.";
        }
        finally { IsBusy = false; }
    }

    partial void OnIsScheduledChanged(bool value) => RemoveScheduleCommand.NotifyCanExecuteChanged();

    protected override void Dispose(bool disposing)
    {
        if (disposing) PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }
}
