// SysManager · ProcessManagerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessManagerViewModel"/>. Verifies initial state,
/// commands, and filter logic. Sorting is handled by DataGrid column headers.
/// </summary>
public class ProcessManagerViewModelTests
{
    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.KillProcessCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
    }

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.NotNull(vm.Processes);
        Assert.NotNull(vm.FilteredProcesses);
    }

    [Fact]
    public void FilterText_DefaultEmpty()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void FilterText_CanBeChanged()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        vm.FilterText = "chrome";
        Assert.Equal("chrome", vm.FilterText);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    // ── ReconcileInto (regression: 1 Hz refresh preserves instances/selection) ──

    private static ProcessEntry Proc(int pid, long mem = 0, double cpu = 0) =>
        new() { Pid = pid, Name = $"p{pid}", MemoryBytes = mem, CpuPercent = cpu };

    [Fact]
    public void ReconcileInto_SurvivingPid_KeepsSameInstanceAndUpdatesMetrics()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var original = Proc(100, mem: 10, cpu: 1);
        original.Icon = null; // identity field set once; must not be touched on update
        target.Add(original);

        // A fresh snapshot for the same PID with new metrics.
        var snapshot = new List<ProcessEntry> { Proc(100, mem: 999, cpu: 42) };

        ProcessManagerViewModel.ReconcileInto(target, snapshot);

        Assert.Single(target);
        // Same instance is reused — this is what lets the DataGrid keep selection.
        Assert.Same(original, target[0]);
        // Volatile metrics updated in place.
        Assert.Equal(999, target[0].MemoryBytes);
        Assert.Equal(42, target[0].CpuPercent);
    }

    [Fact]
    public void ReconcileInto_AddsNewAndRemovesDeadPids()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var keep = Proc(1);
        var dead = Proc(2);
        target.Add(keep);
        target.Add(dead);

        // PID 2 exited, PID 3 is new.
        var snapshot = new List<ProcessEntry> { Proc(1), Proc(3) };

        ProcessManagerViewModel.ReconcileInto(target, snapshot);

        var pids = target.Select(p => p.Pid).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 3 }, pids);
        Assert.Same(keep, target.First(p => p.Pid == 1)); // survivor instance preserved
        Assert.DoesNotContain(dead, target);
    }

    // ── SyncOrdered (regression: filtered view reorders without a Reset) ──

    [Fact]
    public void SyncOrdered_ReordersInPlacePreservingInstances()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var a = Proc(1);
        var b = Proc(2);
        var c = Proc(3);
        target.Add(a);
        target.Add(b);
        target.Add(c);

        // Desired order c, a (b dropped by filter).
        ProcessManagerViewModel.SyncOrdered(target, new List<ProcessEntry> { c, a });

        Assert.Equal(2, target.Count);
        Assert.Same(c, target[0]);
        Assert.Same(a, target[1]);
        Assert.DoesNotContain(b, target);
    }

    [Fact]
    public void SyncOrdered_AddsMissingAtDesiredPosition()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var a = Proc(1);
        target.Add(a);
        var b = Proc(2);

        // b should be inserted before a.
        ProcessManagerViewModel.SyncOrdered(target, new List<ProcessEntry> { b, a });

        Assert.Same(b, target[0]);
        Assert.Same(a, target[1]);
    }
}
