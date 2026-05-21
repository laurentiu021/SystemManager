// SysManager · FileShredderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FileShredderViewModel"/>. Verifies initial state,
/// item management, and default configuration without touching the file system.
/// </summary>
public class FileShredderViewModelTests
{
    private static FileShredderViewModel CreateVm() =>
        new(new FileShredderService());

    [Fact]
    public void Constructor_ItemsStartsEmpty()
    {
        var vm = CreateVm();
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Constructor_SelectedMethodDefaultsToStandard()
    {
        var vm = CreateVm();
        Assert.Equal(ShredMethod.Standard, vm.SelectedMethod);
    }

    [Fact]
    public void Constructor_SelectedMethodValueIs3()
    {
        var vm = CreateVm();
        Assert.Equal(3, (int)vm.SelectedMethod);
    }

    [Fact]
    public void RemoveItem_RemovesFromList()
    {
        var vm = CreateVm();
        var item = new ShredItem
        {
            Path = @"C:\temp\test.txt",
            Name = "test.txt",
            SizeBytes = 1024,
            IsFolder = false
        };
        vm.Items.Add(item);
        Assert.Single(vm.Items);

        vm.RemoveItemCommand.Execute(item);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void RemoveItem_WithNull_DoesNotCrash()
    {
        var vm = CreateVm();
        // Should not throw when passing null
        vm.RemoveItemCommand.Execute(null);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void IsShredding_DefaultsFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.IsShredding);
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Items_CanAddMultiple()
    {
        var vm = CreateVm();
        vm.Items.Add(new ShredItem { Path = @"C:\a.txt", Name = "a.txt", SizeBytes = 100, IsFolder = false });
        vm.Items.Add(new ShredItem { Path = @"C:\b.txt", Name = "b.txt", SizeBytes = 200, IsFolder = false });
        vm.Items.Add(new ShredItem { Path = @"C:\folder", Name = "folder", SizeBytes = 5000, IsFolder = true });
        Assert.Equal(3, vm.Items.Count);
    }
}
