// SysManager · CpuAffinityViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="CpuAffinityViewModel"/>. <see cref="CpuAffinityService"/> is sealed
/// with no interface, so it cannot be substituted; these drive the real service (its
/// <c>GetCores</c> / <c>GetProcesses</c> are read-only enumerations). Coverage is the
/// deterministic surface: construction/topology load, the pure core-selection commands
/// (<c>SelectAllCores</c> / <c>SelectPerformanceCores</c>, which only flip in-memory
/// <see cref="CoreToggle"/> state), Apply/Restore CanExecute gating, and the null-selection
/// branch of <c>OnSelectedProcessChanged</c>. <c>Apply</c> and <c>Restore</c> mutate a real
/// process's affinity, so they are NOT executed.
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
}
