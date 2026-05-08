// SysManager · ShortcutCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

public class ShortcutCleanerViewModelTests
{
    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = new ShortcutCleanerViewModel();
        Assert.False(vm.IsScanning);
        Assert.Equal(0, vm.BrokenCount);
        Assert.Equal(0, vm.SelectedCount);
        Assert.True(vm.MoveToRecycleBin);
        Assert.Contains("Scan", vm.ScanStatus);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var vm = new ShortcutCleanerViewModel();
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", IsSelected = false });
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "B", IsSelected = false });

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.BrokenShortcuts, s => Assert.True(s.IsSelected));
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var vm = new ShortcutCleanerViewModel();
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", IsSelected = true });
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "B", IsSelected = true });

        vm.DeselectAllCommand.Execute(null);

        Assert.All(vm.BrokenShortcuts, s => Assert.False(s.IsSelected));
    }

    [Fact]
    public void BrokenShortcut_Model_DefaultValues()
    {
        var s = new BrokenShortcut();
        Assert.Equal("", s.Name);
        Assert.Equal("", s.ShortcutPath);
        Assert.Equal("", s.TargetPath);
        Assert.Equal("", s.Location);
        Assert.True(s.IsSelected);
    }

    [Fact]
    public void BrokenShortcut_PropertyChanged_Fires()
    {
        var s = new BrokenShortcut();
        string? changedProp = null;
        s.PropertyChanged += (_, e) => changedProp = e.PropertyName;

        s.Name = "Test";
        Assert.Equal("Name", changedProp);

        s.IsSelected = false;
        Assert.Equal("IsSelected", changedProp);
    }
}
