// SysManager · AppBlockerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

public class AppBlockerViewModelTests
{
    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = new AppBlockerViewModel();
        Assert.Equal("", vm.NewExeName);
        Assert.NotNull(vm.BlockedApps);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var vm = new AppBlockerViewModel();
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "a.exe", IsSelected = false });
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "b.exe", IsSelected = false });

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.BlockedApps, a => Assert.True(a.IsSelected));
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var vm = new AppBlockerViewModel();
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "a.exe", IsSelected = true });
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "b.exe", IsSelected = true });

        vm.DeselectAllCommand.Execute(null);

        Assert.All(vm.BlockedApps, a => Assert.False(a.IsSelected));
    }

    [Fact]
    public void BlockedApp_Model_DefaultValues()
    {
        var app = new BlockedApp();
        Assert.Equal("", app.ExecutableName);
        Assert.Equal("", app.FullPath);
        Assert.False(app.IsSelected);
    }

    [Fact]
    public void BlockedApp_PropertyChanged_Fires()
    {
        var app = new BlockedApp();
        string? changed = null;
        app.PropertyChanged += (_, e) => changed = e.PropertyName;

        app.ExecutableName = "test.exe";
        Assert.Equal("ExecutableName", changed);

        app.IsSelected = true;
        Assert.Equal("IsSelected", changed);
    }
}
