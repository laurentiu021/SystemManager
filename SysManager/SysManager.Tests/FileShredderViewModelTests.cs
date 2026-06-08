// SysManager · FileShredderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FileShredderViewModel"/>. Verifies initial state,
/// item management, default configuration, and that the irreversible
/// ShredAll command routes through <see cref="DialogService.Instance"/>.Confirm
/// (audit finding tests #2 — the "every destructive op needs Confirm" contract).
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

    // ---------- irreversible-shred confirmation gate (audit tests #2) ----------

    [Fact]
    public async Task ShredAll_WhenUserDeclinesConfirm_ShredsNothing()
    {
        var file = Path.Combine(Path.GetTempPath(), "smtest_shred_no_" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllText(file, "must survive — user declined");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var vm = CreateVm();
            vm.Items.Add(new ShredItem
            {
                Path = file, Name = Path.GetFileName(file), SizeBytes = 1, IsFolder = false
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.True(File.Exists(file), "File was shredded even though the user declined the confirmation");
            Assert.Single(vm.Items); // item left in place — nothing happened
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredAll_WhenUserConfirms_ShredsSelectedFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "smtest_shred_yes_" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllText(file, "destroy me — user confirmed");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            var vm = CreateVm();
            vm.Items.Add(new ShredItem
            {
                Path = file, Name = Path.GetFileName(file), SizeBytes = 1, IsFolder = false
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(File.Exists(file), "File survived even though the user confirmed the shred");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredAll_WithNoItems_NeverPromptsConfirm()
    {
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            var vm = CreateVm(); // Items empty

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
