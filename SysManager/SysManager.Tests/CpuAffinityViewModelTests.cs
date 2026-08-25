// SysManager · CpuAffinityViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="CpuAffinityViewModel"/>. Two layers of coverage: the
/// deterministic-surface tests drive the real <see cref="CpuAffinityService"/> (its
/// <c>GetCores</c> / <c>GetProcesses</c> are read-only enumerations) and cover
/// construction/topology load, the pure core-selection commands, Apply/Restore
/// CanExecute gating, and the null-selection branch of <c>OnSelectedProcessChanged</c>;
/// the mutating-path tests substitute <see cref="ICpuAffinityService"/> with a
/// deterministic topology + process so <c>Apply</c> and <c>Restore</c> can be executed
/// (asserting <c>TrySetAffinity</c> is called with the correct mask) without touching a
/// real process. The static bitmask helpers stay on the concrete class and are used as-is.
/// </summary>
public class CpuAffinityViewModelTests
{
    // The constructor loads CPU topology + the running-process list asynchronously off the
    // UI thread; await init so Cores/Processes are populated before asserting.
    private static CpuAffinityViewModel NewVm()
    {
        var vm = new CpuAffinityViewModel(new CpuAffinityService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    private static CpuAffinityViewModel NewVm(ICpuAffinityService service)
    {
        var vm = new CpuAffinityViewModel(service);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // A deterministic 4-core homogeneous topology and one target process at pid 4242 whose
    // original affinity is core 0 only (mask 0b0001). Lets Apply/Restore run against a fake.
    private static ICpuAffinityService FourCoreServiceWith(int pid, long originalMask)
    {
        var service = Substitute.For<ICpuAffinityService>();
        service.LogicalProcessorCount.Returns(4);
        service.GetCores().Returns(new List<CpuCore>
        {
            new(0, 0, "Standard"), new(1, 0, "Standard"),
            new(2, 0, "Standard"), new(3, 0, "Standard"),
        });
        service.GetProcesses().Returns(new List<RunningProcess>
        {
            new(pid, "target.exe", originalMask),
        });
        service.GetAffinity(pid).Returns(originalMask);
        return service;
    }

    [Fact]
    public void Constructor_LoadsCores_OnePerLogicalCpu()
    {
        var vm = NewVm();
        // GetCores always returns at least the flat fallback list of Environment.ProcessorCount.
        Assert.Equal(Environment.ProcessorCount, vm.Cores.Count);
        Assert.NotNull(vm.RefreshProcessesCommand);
        Assert.NotNull(vm.ApplyCommand);
        Assert.NotNull(vm.RestoreCommand);
        Assert.NotNull(vm.SelectAllCoresCommand);
        Assert.NotNull(vm.SelectPerformanceCoresCommand);
    }

    [Fact]
    public void Constructor_SetsStatusMessage_AfterInit()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_PopulatesProcessList()
    {
        var vm = NewVm();
        // The current process at minimum is enumerable, so the list is never empty.
        Assert.True(vm.Processes.Count > 0);
    }

    [Fact]
    public void Apply_RequiresSelection()
    {
        var vm = NewVm();
        // HasSelection gate: no SelectedProcess -> Apply is disabled.
        Assert.Null(vm.SelectedProcess);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Restore_RequiresSelectionAndCapturedOriginal()
    {
        var vm = NewVm();
        // CanRestore needs both a selection and a previously-captured original mask. With no
        // selection neither holds, so Restore is disabled.
        Assert.False(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public void SelectAllCores_SelectsEveryCore_AndSetsStatus()
    {
        var vm = NewVm();
        // Clear first so the assertion is meaningful.
        foreach (var c in vm.Cores) c.IsSelected = false;

        vm.SelectAllCoresCommand.Execute(null);

        Assert.All(vm.Cores, c => Assert.True(c.IsSelected));
        Assert.Contains("all cores", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectPerformanceCores_OnNonHybrid_SelectsAllCores()
    {
        var vm = NewVm();
        foreach (var c in vm.Cores) c.IsSelected = false;

        vm.SelectPerformanceCoresCommand.Execute(null);

        if (vm.IsHybrid)
        {
            // On a hybrid CPU only P-cores are selected; E-cores must be left unselected.
            Assert.All(vm.Cores, c => Assert.Equal(c.Core.IsPerformance, c.IsSelected));
            Assert.Contains("P-cores", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // On a homogeneous CPU "performance cores" means every core.
            Assert.All(vm.Cores, c => Assert.True(c.IsSelected));
            Assert.Contains("all cores", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoSelection_DisablesApplyAndRestore()
    {
        // Characterizes the no-selection state deterministically (no real process needed):
        // with SelectedProcess null, the captured-original flag is unset, so HasSelection and
        // CanRestore both gate their commands off. This is the safe half of the
        // OnSelectedProcessChanged behaviour (the populated branch needs a real process to
        // read affinity from, which we deliberately avoid).
        var vm = NewVm();
        Assert.Null(vm.SelectedProcess);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void IsHybrid_MatchesCoreClassification()
    {
        var vm = NewVm();
        // IsHybrid is derived from the loaded cores: true iff both P- and E-cores are present.
        bool expected = vm.Cores.Any(c => c.Core.IsPerformance) && vm.Cores.Any(c => c.Core.IsEfficiency);
        Assert.Equal(expected, vm.IsHybrid);
    }

    [Fact]
    public async Task RefreshProcessesCommand_DoesNotThrow_AndRepopulates()
    {
        var vm = NewVm();
        await vm.RefreshProcessesCommand.ExecuteAsync(null);
        Assert.True(vm.Processes.Count > 0);
        Assert.False(vm.IsBusy);
    }

    // ── Mutating-path tests (substituted ICpuAffinityService) ──────────────

    [Fact]
    public void Apply_AfterSelectAllCores_CallsTrySetAffinityWithFullMask()
    {
        const int pid = 4242;
        const long originalMask = 0b0001; // core 0 only
        var service = FourCoreServiceWith(pid, originalMask);
        service.TrySetAffinity(pid, Arg.Any<long>(), out Arg.Any<string>()).Returns(true);

        var vm = NewVm(service);
        // Selecting the loaded process captures its original mask via GetAffinity(pid).
        vm.SelectedProcess = vm.Processes.Single(p => p.ProcessId == pid);
        vm.SelectAllCoresCommand.Execute(null);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.ApplyCommand.Execute(null);

        // All four cores selected → mask 0b1111 (the low 4 bits).
        long expectedMask = CpuAffinityService.AllCoresMask(4);
        service.Received(1).TrySetAffinity(pid, expectedMask, out Arg.Any<string>());
        Assert.Contains("Pinned", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_OnServiceFailure_SurfacesErrorMessage()
    {
        const int pid = 4242;
        var service = FourCoreServiceWith(pid, 0b0001);
        // Service rejects the change and reports an error via the out parameter.
        service.TrySetAffinity(pid, Arg.Any<long>(), out Arg.Any<string>())
            .Returns(call => { call[2] = "needs administrator rights."; return false; });

        var vm = NewVm(service);
        vm.SelectedProcess = vm.Processes.Single(p => p.ProcessId == pid);
        vm.SelectAllCoresCommand.Execute(null);

        vm.ApplyCommand.Execute(null);

        service.Received(1).TrySetAffinity(pid, Arg.Any<long>(), out Arg.Any<string>());
        Assert.Equal("needs administrator rights.", vm.StatusMessage);
    }

    [Fact]
    public void Restore_CallsTrySetAffinityWithCapturedOriginalMask()
    {
        const int pid = 4242;
        const long originalMask = 0b0010; // core 1 only — the captured original
        var service = FourCoreServiceWith(pid, originalMask);
        service.TrySetAffinity(pid, Arg.Any<long>(), out Arg.Any<string>()).Returns(true);

        var vm = NewVm(service);
        // Selecting captures originalMask; flipping the selection proves Restore uses the
        // captured value, not the current checkbox state.
        vm.SelectedProcess = vm.Processes.Single(p => p.ProcessId == pid);
        vm.SelectAllCoresCommand.Execute(null);
        Assert.True(vm.RestoreCommand.CanExecute(null));

        vm.RestoreCommand.Execute(null);

        service.Received(1).TrySetAffinity(pid, originalMask, out Arg.Any<string>());
        // The checkboxes were reset to reflect the restored original mask (core 1 only).
        Assert.All(vm.Cores, c => Assert.Equal(c.Index == 1, c.IsSelected));
        Assert.Contains("Restored", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Progress feedback (regression) ──
    // The view binds a progress bar to IsBusy and the sidebar spinner reads the same flag, but this
    // VM never set it — so enumerating every process (reading each one's affinity) gave the user no
    // feedback whatsoever. The bar was structurally incapable of appearing.

    [Fact]
    public async Task RefreshProcesses_RaisesIsBusyWhileWorkingAndClearsItAfter()
    {
        const int pid = 4242;
        var service = FourCoreServiceWith(pid, 0b0001);
        var vm = NewVm(service);

        var seen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsBusy)) seen.Add(vm.IsBusy);
        };

        await vm.RefreshProcessesCommand.ExecuteAsync(null);

        // Observed via the change notifications rather than a sampled read: the operation completes
        // too fast to catch mid-flight, but the flag must still have gone up and then down.
        Assert.Equal([true, false], seen);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshProcesses_SetsTheBarToIndeterminate()
    {
        // There is no percentage to report for a process enumeration, so the bar must be marquee —
        // a determinate bar stuck at 0 reads as "stalled".
        const int pid = 4242;
        var vm = NewVm(FourCoreServiceWith(pid, 0b0001));

        var seen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsProgressIndeterminate)) seen.Add(vm.IsProgressIndeterminate);
        };

        await vm.RefreshProcessesCommand.ExecuteAsync(null);

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void AfterConstruction_TheBusyFlagIsClear()
    {
        // The initial topology + process load also sets the flag; it must be released once init ends,
        // or the bar would spin forever on a freshly opened tab.
        var vm = NewVm(FourCoreServiceWith(4242, 0b0001));

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task RefreshProcesses_WhenTheServiceThrows_StillClearsTheBusyFlag()
    {
        // A failed scan must not leave the bar spinning forever.
        var service = Substitute.For<ICpuAffinityService>();
        service.LogicalProcessorCount.Returns(4);
        service.GetCores().Returns([new CpuCore(0, 0, "Standard")]);
        service.GetProcesses().Returns(_ => throw new InvalidOperationException("scan failed"));

        var vm = new CpuAffinityViewModel(service);
        await vm.InitializationComplete;   // the guarded helper logs the failure

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    // ---------- process filter (#1608) ----------
    // A multi-process fake so the name/PID filter can be exercised deterministically. Homogeneous
    // 4-core topology; the process masks vary so the pinned-marker tests have something to read.
    private static ICpuAffinityService ServiceWithProcesses(params RunningProcess[] procs)
    {
        var service = Substitute.For<ICpuAffinityService>();
        service.LogicalProcessorCount.Returns(4);
        service.GetCores().Returns(new List<CpuCore>
        {
            new(0, 0, "Standard"), new(1, 0, "Standard"),
            new(2, 0, "Standard"), new(3, 0, "Standard"),
        });
        service.GetProcesses().Returns(procs.ToList());
        return service;
    }

    [Fact]
    public void Filter_ByName_NarrowsToMatchingProcesses()
    {
        var vm = NewVm(ServiceWithProcesses(
            new(10, "chrome", 0), new(11, "svchost", 0), new(12, "chromium", 0)));

        Assert.Equal(3, vm.Processes.Count);

        vm.FilterText = "chrom";

        Assert.Equal(new[] { "chrome", "chromium" }, vm.Processes.Select(p => p.Name).OrderBy(n => n));
        Assert.DoesNotContain(vm.Processes, p => p.Name == "svchost");
    }

    [Fact]
    public void Filter_ByPid_Matches()
    {
        var vm = NewVm(ServiceWithProcesses(
            new(4242, "game", 0), new(15, "other", 0)));

        vm.FilterText = "4242";

        Assert.Equal("game", Assert.Single(vm.Processes).Name);
    }

    [Fact]
    public void Filter_IsCaseInsensitive()
    {
        var vm = NewVm(ServiceWithProcesses(new(10, "Discord", 0), new(11, "steam", 0)));

        vm.FilterText = "DISCORD";

        Assert.Equal("Discord", Assert.Single(vm.Processes).Name);
    }

    [Fact]
    public void Filter_Cleared_RestoresTheFullList()
    {
        var vm = NewVm(ServiceWithProcesses(new(10, "a", 0), new(11, "b", 0), new(12, "c", 0)));

        vm.FilterText = "a";
        Assert.Single(vm.Processes);

        vm.FilterText = "";
        Assert.Equal(3, vm.Processes.Count);   // rebuilt from the backing list, not lost
    }

    [Fact]
    public async Task Refresh_PreservesTheSelectionEvenWhenTheProcessMaskChanged()
    {
        // The selected process is still running on refresh, but its affinity changed (say, it was just
        // pinned), so the new record is NOT value-equal to the old one. Only reselect-by-PID keeps it
        // selected — a record with value equality would otherwise drop the selection to null the moment
        // the mask differs, which is exactly the moment the user cares about it.
        var service = Substitute.For<ICpuAffinityService>();
        service.LogicalProcessorCount.Returns(4);
        service.GetCores().Returns(new List<CpuCore>
        {
            new(0, 0, "Standard"), new(1, 0, "Standard"),
            new(2, 0, "Standard"), new(3, 0, "Standard"),
        });
        service.GetProcesses().Returns(
            new List<RunningProcess> { new(4242, "game", 0), new(15, "other", 0) },       // first load
            new List<RunningProcess> { new(4242, "game", 0b0001), new(15, "other", 0) }); // after refresh: mask changed
        var vm = NewVm(service);
        vm.SelectedProcess = vm.Processes.First(p => p.ProcessId == 4242);

        await vm.RefreshProcessesCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedProcess);
        Assert.Equal(4242, vm.SelectedProcess!.ProcessId);
        Assert.Equal(0b0001, vm.SelectedProcess.AffinityMask);   // the refreshed record, reselected by PID
    }

    // ---------- pinned marker: RunningProcess.DescribeAffinity (pure) ----------
    // Neutral "N of M cores" only for a real subset; nothing for unreadable (0) or all-cores.

    [Theory]
    [InlineData(0b0001, 4, "proc (7) — 1 of 4 cores")]
    [InlineData(0b0101, 4, "proc (7) — 2 of 4 cores")]
    [InlineData(0b0011, 4, "proc (7) — 2 of 4 cores")]
    public void DescribeAffinity_ForASubset_ReportsTheCoreCount(long mask, int logical, string expected)
    {
        Assert.Equal(expected, RunningProcess.DescribeAffinity("proc", 7, mask, logical));
    }

    [Theory]
    [InlineData(0, 4)]              // unreadable — no suffix
    [InlineData(0b1111, 4)]         // all four cores — the default, not pinned
    public void DescribeAffinity_ForNoneOrAllCores_HasNoSuffix(long mask, int logical)
    {
        Assert.Equal("proc (7)", RunningProcess.DescribeAffinity("proc", 7, mask, logical));
    }

    [Fact]
    public void DescribeAffinity_AllCoresPlusStrayBit_StillHasNoSuffix()
    {
        // A stray high bit outside the machine's cores must not make an all-cores mask look like a
        // subset. 0b1111 within 4 cores is still "all", regardless of the extra bit — the all-cores
        // check masks with allCores first.
        Assert.Equal("proc (7)", RunningProcess.DescribeAffinity("proc", 7, unchecked((long)0xF000_0000_0000_000F), 4));
    }

    [Fact]
    public void DescribeAffinity_SubsetPlusStrayBit_CountsOnlyTheRealCores()
    {
        // bit 0 (a real core) + bit 60 (outside the 4 cores). This is a genuine subset, so it reaches
        // the count — which must ignore the stray bit and report 1, not 2. Pins that PopCount runs over
        // (mask & allCores), not the raw mask.
        Assert.Equal("proc (7) — 1 of 4 cores",
            RunningProcess.DescribeAffinity("proc", 7, unchecked((long)0x1000_0000_0000_0001), 4));
    }

    [Fact]
    public void PinnedDisplay_UsesTheMachineCoreCount()
    {
        // The instance property feeds Environment.ProcessorCount to the pure helper. A single-core
        // mask on a multi-core machine is always a subset, so the suffix must appear.
        var single = new RunningProcess(7, "proc", 0b0001);
        if (Environment.ProcessorCount > 1)
            Assert.Contains("of " + Environment.ProcessorCount + " cores", single.PinnedDisplay);
        // And an all-cores mask never gets a suffix.
        var all = new RunningProcess(7, "proc", CpuAffinityService.AllCoresMask(Environment.ProcessorCount));
        Assert.Equal("proc (7)", all.PinnedDisplay);
    }
}
