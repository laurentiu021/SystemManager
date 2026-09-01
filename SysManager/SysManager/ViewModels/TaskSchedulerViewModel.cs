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

    // TWO sources, deliberately. The list scan and the per-selection run-info query are unrelated
    // operations: arrow-keying down the grid must supersede the previous run-info query without
    // aborting a full refresh that happens to be running, and Cancel must stop the scan without
    // killing the run-info query for the row the user just landed on.
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _runInfoCts;

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
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    private bool NotBusy => !IsBusy;

    // Composed, not replaced: Enable/Disable already required a selection, and now also has to wait
    // for an in-flight scan — both service calls share one runspace-per-call runner, so overlapping
    // them lets two invocations interleave.
    private bool CanToggle => !IsBusy && HasSelection;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(SelectedTask))
        {
            RefreshCommand.NotifyCanExecuteChanged();
            ToggleEnabledCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading scheduled tasks…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            _all = (await _service.ListTasksAsync(_cts.Token).ConfigureAwait(true)).ToList();
            ApplyFilter();
            StatusMessage = _all.Count == 0
                ? "No scheduled tasks found."
                : $"{_all.Count} tasks. Select one to see when it last ran.";
        }
        catch (OperationCanceledException)
        {
            // The user pressed Cancel. Whatever was already listed stays on screen; saying so beats
            // a half-loaded grid with a stale "Loading…" underneath it.
            StatusMessage = _all.Count == 0
                ? "Cancelled before any tasks were listed."
                : $"Cancelled. Showing the {_all.Count} tasks listed before you stopped it.";
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

    // Guards the in-place re-selection below (SelectedTask = withInfo): without it, that
    // reassignment re-enters OnSelectedTaskChanged and fires a second per-task run-info query.
    private bool _reassigningSelection;

    partial void OnSelectedTaskChanged(ScheduledTaskInfo? value)
    {
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        if (_reassigningSelection) return;
        if (value is not null) _ = LoadRunInfoAsync(value);
    }

    private async Task LoadRunInfoAsync(ScheduledTaskInfo task)
    {
        // Each selection change supersedes the last. Holding an arrow key down used to queue one
        // PowerShell round-trip per row passed over, all of them still running while the user had
        // long since moved on.
        _runInfoCts?.Cancel();
        _runInfoCts?.Dispose();
        _runInfoCts = new CancellationTokenSource();
        var ct = _runInfoCts.Token;

        ScheduledTaskInfo withInfo;
        try
        {
            withInfo = await _service.LoadRunInfoAsync(task, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, or the view closed. Not a failure, and there is
            // nothing to show: the row this query was for is no longer the one being looked at.
            return;
        }

        // Update the item in place if it's still the selection.
        if (ReferenceEquals(SelectedTask, task))
        {
            int idx = Tasks.IndexOf(task);
            _reassigningSelection = true;
            try
            {
                if (idx >= 0) Tasks[idx] = withInfo;
                SelectedTask = withInfo;
            }
            finally { _reassigningSelection = false; }
        }
    }

    private bool HasSelection => SelectedTask is not null;

    [RelayCommand(CanExecute = nameof(CanToggle))]
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

    /// <summary>
    /// Stops the task-list scan. Deliberately does NOT cover Enable/Disable: that script writes the
    /// new state and then reads it back, so a cancel landing between the two would leave the task
    /// toggled while the grid still showed the old value — a worse outcome than waiting out a call
    /// that takes a moment.
    /// </summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Cancel();
            _cts?.Dispose();
            _runInfoCts?.Cancel();
            _runInfoCts?.Dispose();
        }
        base.Dispose(disposing);
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
