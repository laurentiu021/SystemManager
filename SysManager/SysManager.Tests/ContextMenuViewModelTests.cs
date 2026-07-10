// SysManager · ContextMenuViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ContextMenuViewModel"/>. Verifies constructor defaults,
/// filter state, counter consistency, and command wiring. The VM auto-scans the
/// registry on construction; <see cref="NewVm"/> awaits <see cref="ViewModelBase.InitializationComplete"/>
/// so assertions observe a settled state deterministically (no race with the scan).
/// </summary>
public class ContextMenuViewModelTests
{
    private static ContextMenuViewModel NewVm()
    {
        var vm = new ContextMenuViewModel(new ContextMenuService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_LocationFilters_ContainsAllPlusSpecific()
    {
        var vm = NewVm();
        Assert.Contains("All", vm.LocationFilters);
        Assert.Contains("Files", vm.LocationFilters);
        Assert.Contains("Folders", vm.LocationFilters);
        Assert.Contains("Directory Background", vm.LocationFilters);
        Assert.Contains("Desktop", vm.LocationFilters);
        Assert.Equal(5, vm.LocationFilters.Count);
    }

    [Fact]
    public void Constructor_SelectedLocation_DefaultsToAll()
    {
        var vm = NewVm();
        Assert.Equal("All", vm.SelectedLocation);
    }

    [Fact]
    public void Constructor_FilterText_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void AfterInit_Entries_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Entries);
    }

    [Fact]
    public void AfterInit_TotalCount_MatchesEntries()
    {
        var vm = NewVm();
        Assert.Equal(vm.Entries.Count, vm.TotalCount);
    }

    [Fact]
    public void AfterInit_EnabledPlusDisabled_EqualsTotalCount()
    {
        var vm = NewVm();
        Assert.Equal(vm.TotalCount, vm.EnabledCount + vm.DisabledCount);
    }

    [Fact]
    public void AfterInit_IsBusy_IsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Commands_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.ToggleEntryCommand);
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.ApplyPresetCommand);
    }

    [Fact]
    public void Constructor_ActivePresetId_DefaultsToMenuStyle()
    {
        var vm = NewVm();
        Assert.Contains(vm.ActivePresetId, new[] { "win10", "win11" });
    }

    // ---------- re-entrancy guard (regression: overlapping scan/preset runs) ----------

    [Fact]
    public void LongRunningCommands_DisabledWhileBusy()
    {
        // Scan, Refresh and ApplyPreset all mutate the shared _allEntries list off the UI
        // thread (ApplyPreset also restarts Explorer). The NotBusy gate disables them while
        // one runs so overlapping runs can't corrupt that list or race two restarts.
        // Drive IsBusy explicitly rather than asserting the post-construction baseline:
        // the constructor kicks off an async scan that briefly sets IsBusy itself.
        var vm = NewVm();

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.ApplyPresetCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.ApplyPresetCommand.CanExecute(null));
    }
}
