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
    private readonly ICpuAffinityService _service;
    private long _originalMask;
    private bool _hasOriginal;

    public BulkObservableCollection<RunningProcess> Processes { get; } = new();
    public BulkObservableCollection<CoreToggle> Cores { get; } = new();

    [ObservableProperty] private RunningProcess? _selectedProcess;
    [ObservableProperty] private bool _isHybrid;

    public CpuAffinityViewModel(ICpuAffinityService service)
    {
        _service = service;
        StatusMessage = "Reading CPU topology…";
        InitializeAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        var cores = await Task.Run(_service.GetCores).ConfigureAwait(true);
        IsHybrid = cores.Any(c => c.IsPerformance) && cores.Any(c => c.IsEfficiency);
        Cores.ReplaceWith(cores.Select(c => new CoreToggle { Core = c }));
        await RefreshProcessesAsync();
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var procs = await Task.Run(_service.GetProcesses).ConfigureAwait(true);
        Processes.ReplaceWith(procs);
        StatusMessage = IsHybrid
            ? "Hybrid CPU detected — P-cores and E-cores are labelled. Pick a process."
            : "Pick a process, choose cores, then apply.";
    }

    partial void OnSelectedProcessChanged(RunningProcess? value)
    {
        _hasOriginal = false;
        if (value is null) return;

        long? mask = _service.GetAffinity(value.ProcessId);
        if (mask is { } m)
        {
            _originalMask = m;
            _hasOriginal = true;
            foreach (var c in Cores) c.IsSelected = CpuAffinityService.IsCoreInMask(m, c.Index);
        }
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection => SelectedProcess is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Apply()
    {
        var proc = SelectedProcess;
        if (proc is null) return;

        long mask = CpuAffinityService.MaskFromIndices(Cores.Where(c => c.IsSelected).Select(c => c.Index));
        if (_service.TrySetAffinity(proc.ProcessId, mask, out string error))
        {
            Log.Information("Set CPU affinity 0x{Mask:X} on {Name} ({Pid})", mask, proc.Name, proc.ProcessId);
            StatusMessage = $"Pinned {proc.Name} to {CountBits(mask)} core(s). Reverts when the process exits.";
        }
        else
        {
            StatusMessage = error;
        }
    }

    private bool CanRestore => SelectedProcess is not null && _hasOriginal;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        var proc = SelectedProcess;
        if (proc is null || !_hasOriginal) return;
        if (_service.TrySetAffinity(proc.ProcessId, _originalMask, out string error))
        {
            foreach (var c in Cores) c.IsSelected = CpuAffinityService.IsCoreInMask(_originalMask, c.Index);
            StatusMessage = $"Restored {proc.Name} to its original cores.";
        }
        else
        {
            StatusMessage = error;
        }
    }

    [RelayCommand]
    private void SelectPerformanceCores()
    {
        foreach (var c in Cores) c.IsSelected = IsHybrid ? c.Core.IsPerformance : true;
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
