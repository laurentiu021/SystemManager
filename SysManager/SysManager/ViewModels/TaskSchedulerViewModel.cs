// SysManager · TaskSchedulerViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Task Scheduler tab. Lists Windows scheduled tasks with a
/// safety classification (Third-party / Telemetry / System), and enables or disables
/// the selected task. Disabling is reversible; tasks are never deleted. System tasks
/// require a confirmation warning; changes need admin and are verified by read-back.
/// </summary>
public sealed partial class TaskSchedulerViewModel : ViewModelBase
{
    private readonly TaskSchedulerService _service;
    private List<ScheduledTaskInfo> _all = [];

    public BulkObservableCollection<ScheduledTaskInfo> Tasks { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private ScheduledTaskInfo? _selectedTask;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _hideSystemTasks;

    public TaskSchedulerViewModel(TaskSchedulerService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Loading scheduled tasks…";
        InitializeAsync(RefreshAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading scheduled tasks…";
        try
        {
            _all = (await _service.ListTasksAsync().ConfigureAwait(true)).ToList();
            ApplyFilter();
            StatusMessage = _all.Count == 0
                ? "No scheduled tasks found."
                : $"{_all.Count} tasks. Select one to see when it last ran.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnHideSystemTasksChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<ScheduledTaskInfo> q = _all;
        if (HideSystemTasks) q = q.Where(t => !t.IsSystem);
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            string f = Filter.Trim();
            q = q.Where(t => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || t.Path.Contains(f, StringComparison.OrdinalIgnoreCase));
        }
        Tasks.ReplaceWith(q.ToList());
        ToggleEnabledCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTaskChanged(ScheduledTaskInfo? value)
    {
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        if (value is not null) _ = LoadRunInfoAsync(value);
    }

    private async Task LoadRunInfoAsync(ScheduledTaskInfo task)
    {
        var withInfo = await _service.LoadRunInfoAsync(task).ConfigureAwait(true);
        // Update the item in place if it's still the selection.
        if (ReferenceEquals(SelectedTask, task))
        {
            int idx = Tasks.IndexOf(task);
            if (idx >= 0) Tasks[idx] = withInfo;
            SelectedTask = withInfo;
        }
    }

    private bool HasSelection => SelectedTask is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleEnabledAsync()
    {
        var task = SelectedTask;
        if (task is null) return;

        bool enabling = !task.IsEnabled;
        string verb = enabling ? "Enable" : "Disable";

        string message = task.IsSystem
            ? $"{verb} the Windows system task \"{task.Name}\"?\n\nThis is a system task — disabling it may affect Windows features. It can be re-enabled at any time."
            : $"{verb} the task \"{task.Name}\"?\n\nIt can be re-enabled at any time.";
        if (!DialogService.Instance.Confirm(message, $"{verb} Task — Confirm")) return;

        var result = await _service.SetEnabledAsync(task.Name, task.Path, enabling).ConfigureAwait(true);
        if (result is not null)
        {
            Log.Information("Task {Path} {Verb}d", task.FullPath, verb);
            ReplaceTask(task, result);
            StatusMessage = $"{result.Name} is now {(result.IsEnabled ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusMessage = $"Couldn't change \"{task.Name}\" — this usually needs administrator rights.";
        }
    }

    private void ReplaceTask(ScheduledTaskInfo oldTask, ScheduledTaskInfo newTask)
    {
        int allIdx = _all.FindIndex(t => t.FullPath == oldTask.FullPath);
        if (allIdx >= 0) _all[allIdx] = newTask;
        int idx = Tasks.IndexOf(oldTask);
        if (idx >= 0) Tasks[idx] = newTask;
        SelectedTask = newTask;
    }
}
