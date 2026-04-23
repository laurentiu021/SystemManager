// SysManager · ProcessManagerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessManagerViewModel"/>. Verifies initial state,
/// commands, and filter/sort logic.
/// </summary>
public class ProcessManagerViewModelTests
{
    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = new ProcessManagerViewModel();
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.KillProcessCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
        Assert.NotNull(vm.SortByNameCommand);
        Assert.NotNull(vm.SortByMemoryCommand);
        Assert.NotNull(vm.SortByPidCommand);
    }

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = new ProcessManagerViewModel();
        Assert.NotNull(vm.Processes);
        Assert.NotNull(vm.FilteredProcesses);
    }

    [Fact]
    public void FilterText_DefaultEmpty()
    {
        var vm = new ProcessManagerViewModel();
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void SortBy_DefaultMemory()
    {
        var vm = new ProcessManagerViewModel();
        Assert.Equal("Memory", vm.SortBy);
    }

    [Fact]
    public void FilterText_CanBeChanged()
    {
        var vm = new ProcessManagerViewModel();
        vm.FilterText = "chrome";
        Assert.Equal("chrome", vm.FilterText);
    }

    [Fact]
    public void SortBy_CanBeChanged()
    {
        var vm = new ProcessManagerViewModel();
        vm.SortBy = "Name";
        Assert.Equal("Name", vm.SortBy);
    }
}
