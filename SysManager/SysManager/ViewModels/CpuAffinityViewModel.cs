// SysManager · CpuAffinityViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>A selectable logical CPU with its P/E label and checked state.</summary>
public sealed partial class CoreToggle : ObservableObject
{
    public required CpuCore Core { get; init; }
    [ObservableProperty] private bool _isSelected = true;
    public int Index => Core.LogicalIndex;
    public string Label => Core.Display;
    public string TypeLabel => Core.CoreType;
}

/// <summary>
/// ViewModel for the CPU Core Affinity tab. Lists running processes and lets the user
/// pin one to specific logical CPUs, with P-core / E-core labels on Intel hybrid CPUs.
/// Affinity is per-running-process and lost on exit, so it's temporary and reversible;
/// no admin for your own processes.
/// </summary>
public sealed partial class CpuAffinityViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshProcessesCommand;

    private readonly ICpuAffinityService _service;

    /// <summary>
    /// The affinity each process had when SysManager FIRST saw it this session, keyed by pid, name and
    /// start time. Captured once and never overwritten, which is the entire point: this used to be a single
    /// field re-read on every selection change, so a Refresh after Apply re-baselined "original" to the mask
    /// the user had just pinned — Restore then wrote the pinned value back and still reported
    /// "Restored … to its original cores". Keyed on more than the pid because Windows reuses pids: a
    /// recycled pid is a different process and must not inherit the old one's original mask. The start time
    /// tells them apart even when the new process has the same name, as a restarted program does (#2514).
    /// <para>One entry per process the user actually selects, so it is bounded by clicks rather than by
    /// the process list; a refresh re-selecting the same process adds nothing.</para>
    /// </summary>
    private readonly Dictionary<(int Pid, string Name, DateTime? StartTime), long> _originalMasks = [];

    // The full list; Processes is the filtered view the picker binds. Kept separate so typing in the
    // filter never loses a process, exactly as ServicesViewModel keeps _allServices behind Services.
    private List<RunningProcess> _allProcesses = [];

    public BulkObservableCollection<RunningProcess> Processes { get; } = new();
    public BulkObservableCollection<CoreToggle> Cores { get; } = new();

    [ObservableProperty] private RunningProcess? _selectedProcess;
    [ObservableProperty] private bool _isHybrid;

    // A 300-row dropdown of svchost entries is a dead end for finding one game; this is the same
    // name/PID filter the other list tabs (Services, Task Scheduler, Windows Features) already have.
    [ObservableProperty] private string _filterText = "";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public CpuAffinityViewModel(ICpuAffinityService service)
    {
        _service = service;
        StatusMessage = "Reading CPU topology…";
        InitializeAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var cores = await Task.Run(_service.GetCores).ConfigureAwait(true);
            IsHybrid = cores.Any(c => c.IsPerformance) && cores.Any(c => c.IsEfficiency);
            Cores.ReplaceWith(cores.Select(c => new CoreToggle { Core = c }));
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }

        // Outside the block above, not nested inside it: RefreshProcessesAsync owns the flag for its
        // own span, and overlapping the two would have it cleared while this one is still running.
        await RefreshProcessesAsync();
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        // Enumerating every process and reading each one's affinity takes real time on a busy
        // machine, and the view already binds a progress bar (plus the sidebar spinner) to IsBusy —
        // without this the click produced no feedback at all.
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            // Preserve the selection across a refresh by PID and start time: the list is a fresh set of records,
            // so the previously-selected instance is gone, but the process it named may still be running. The
            // start time is what tells that process from a new one Windows has given the same PID (#2514).
            var selected = SelectedProcess;

            _allProcesses = (await Task.Run(_service.GetProcesses).ConfigureAwait(true)).ToList();
            ApplyFilter();

            if (selected is not null)
                SelectedProcess = Processes.FirstOrDefault(
                    p => p.ProcessId == selected.ProcessId && p.StartTime == selected.StartTime);

            StatusMessage = IsHybrid
                ? "Hybrid CPU detected — P-cores and E-cores are labelled. Pick a process."
                : "Pick a process, choose cores, then apply.";
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    /// <summary>
    /// Narrows the picker to processes whose name or PID contains the filter text. Rebuilt from
    /// <see cref="_allProcesses"/> every time, so clearing the box restores the full list.
    /// </summary>
    private void ApplyFilter()
    {
        var q = FilterText?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            Processes.ReplaceWith(_allProcesses);
            return;
        }

        Processes.ReplaceWith(_allProcesses.Where(p =>
            p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal)));
    }

    partial void OnSelectedProcessChanged(RunningProcess? value)
    {
        if (value is null)
        {
            ApplyCommand.NotifyCanExecuteChanged();
            RestoreCommand.NotifyCanExecuteChanged();
            return;
        }

        long? mask = _service.GetAffinity(value.ProcessId, value.StartTime);
        if (mask is { } m)
        {
            // Capture the original ONCE per process. TryAdd, not an assignment: re-selecting a process
            // — which a Refresh now does automatically to preserve the selection — must not treat the
            // mask SysManager itself just applied as the value to restore to.
            _originalMasks.TryAdd(OriginalKey(value), m);

            // The checkboxes always mirror the LIVE mask, so the grid shows what is true right now.
            // Only the restore target is remembered.
            foreach (var c in Cores) c.IsSelected = CpuAffinityService.IsCoreInMask(m, c.Index);
        }
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private static (int Pid, string Name, DateTime? StartTime) OriginalKey(RunningProcess p) =>
        (p.ProcessId, p.Name, p.StartTime);

    private bool HasSelection => SelectedProcess is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ApplyAsync()
    {
        var proc = SelectedProcess;
        if (proc is null) return;

        long mask = CpuAffinityService.MaskFromIndices(Cores.Where(c => c.IsSelected).Select(c => c.Index));
        if (_service.TrySetAffinity(proc.ProcessId, proc.StartTime, mask, out string error))
        {
            Log.Information("Set CPU affinity 0x{Mask:X} on {Name} ({Pid})", mask, proc.Name, proc.ProcessId);
            StatusMessage = $"Pinned {proc.Name} to {CountBits(mask)} core(s). Reverts when the process exits.";
        }
        else
        {
            StatusMessage = error;
            await RefreshIfClosedAsync(proc);
        }
    }

    private bool CanRestore =>
        SelectedProcess is not null && _originalMasks.ContainsKey(OriginalKey(SelectedProcess));

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var proc = SelectedProcess;
        if (proc is null) return;
        if (!_originalMasks.TryGetValue(OriginalKey(proc), out long original)) return;

        if (_service.TrySetAffinity(proc.ProcessId, proc.StartTime, original, out string error))
        {
            foreach (var c in Cores) c.IsSelected = CpuAffinityService.IsCoreInMask(original, c.Index);
            StatusMessage = $"Restored {proc.Name} to its original cores.";
        }
        else
        {
            StatusMessage = error;
            await RefreshIfClosedAsync(proc);
        }
    }

    /// <summary>
    /// After a change that failed, refreshes the list if the process it was meant for has closed, and says so.
    /// </summary>
    /// <remarks>
    /// The list is only as fresh as its last refresh, and Windows gives a closed process's PID to the next one
    /// started. The service checks the start time before it changes anything, so a new process with the old PID is
    /// left alone (#2514). The refresh then takes the closed process off the list, and the selection with it, so
    /// the next Apply cannot be aimed at it again. Not after every failure: a change refused for want of
    /// administrator rights names a process that is still running.
    /// </remarks>
    private async Task RefreshIfClosedAsync(RunningProcess proc)
    {
        if (!_service.HasExited(proc.ProcessId, proc.StartTime)) return;

        await RefreshProcessesAsync();
        StatusMessage = $"{proc.Display} had already closed, so nothing was changed.";
    }

    [RelayCommand]
    private void SelectPerformanceCores()
    {
        foreach (var c in Cores) c.IsSelected = !IsHybrid || c.Core.IsPerformance;
        StatusMessage = IsHybrid ? "Selected P-cores." : "Selected all cores.";
    }

    [RelayCommand]
    private void SelectAllCores()
    {
        foreach (var c in Cores) c.IsSelected = true;
        StatusMessage = "Selected all cores.";
    }

    private static int CountBits(long mask) => System.Numerics.BitOperations.PopCount((ulong)mask);
}
