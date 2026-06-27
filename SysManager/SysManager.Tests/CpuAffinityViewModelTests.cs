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
}
