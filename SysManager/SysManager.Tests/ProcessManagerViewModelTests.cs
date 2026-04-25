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

    [Fact]
    public void SortByCpuCommand_Exists()
    {
        var vm = new ProcessManagerViewModel();
        Assert.NotNull(vm.SortByCpuCommand);
    }

    [Fact]
    public void SortByCpu_SetsSortByToCPU()
    {
        var vm = new ProcessManagerViewModel();
        vm.SortByCpuCommand.Execute(null);
        Assert.Equal("CPU", vm.SortBy);
    }

    [Fact]
    public void SortByName_SetsSortByToName()
    {
        var vm = new ProcessManagerViewModel();
        vm.SortByNameCommand.Execute(null);
        Assert.Equal("Name", vm.SortBy);
    }

    [Fact]
    public void SortByMemory_SetsSortByToMemory()
    {
        var vm = new ProcessManagerViewModel();
        vm.SortByMemoryCommand.Execute(null);
        Assert.Equal("Memory", vm.SortBy);
    }

    [Fact]
    public void SortByPid_SetsSortByToPID()
    {
        var vm = new ProcessManagerViewModel();
        vm.SortByPidCommand.Execute(null);
        Assert.Equal("PID", vm.SortBy);
    }
}
