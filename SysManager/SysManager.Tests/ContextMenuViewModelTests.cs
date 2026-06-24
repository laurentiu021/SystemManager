// SysManager · ContextMenuViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ContextMenuViewModel"/>. Verifies initial state,
/// filter defaults, and counter logic without scanning the registry.
/// </summary>
public class ContextMenuViewModelTests
{
    private static ContextMenuViewModel NewVm() => new(new ContextMenuService());

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
    public void Constructor_Entries_StartsEmpty()
    {
        var vm = NewVm();
        // Before scan, entries should be empty
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void Constructor_TotalCount_DefaultsToZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void Constructor_EnabledCount_DefaultsToZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.EnabledCount);
    }

    [Fact]
    public void Constructor_DisabledCount_DefaultsToZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.DisabledCount);
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
}
