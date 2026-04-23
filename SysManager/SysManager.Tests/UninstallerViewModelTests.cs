// SysManager · UninstallerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerViewModel"/>. Verifies initial state,
/// commands, and filter logic.
/// </summary>
public class UninstallerViewModelTests
{
    private static UninstallerViewModel CreateVm() => new(new PowerShellRunner());

    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.UninstallSelectedCommand);
        Assert.NotNull(vm.CancelCommand);
        Assert.NotNull(vm.SelectAllCommand);
        Assert.NotNull(vm.DeselectAllCommand);
    }

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.AllApps);
        Assert.NotNull(vm.FilteredApps);
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void FilterText_DefaultEmpty()
    {
        var vm = CreateVm();
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = CreateVm();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void FilterText_CanBeChanged()
    {
        var vm = CreateVm();
        vm.FilterText = "chrome";
        Assert.Equal("chrome", vm.FilterText);
    }

    [Fact]
    public void AppCount_DefaultZero()
    {
        var vm = CreateVm();
        Assert.Equal(0, vm.AppCount);
    }
}
