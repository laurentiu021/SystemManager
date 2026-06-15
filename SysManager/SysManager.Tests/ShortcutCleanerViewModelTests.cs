// SysManager · ShortcutCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

// Serialized: the DeleteSelected gate tests swap the static DialogService.Instance.
[Collection("DialogService")]
public class ShortcutCleanerViewModelTests
{
    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = new ShortcutCleanerViewModel(new Services.ShortcutCleanerService());
        Assert.False(vm.IsScanning);
        Assert.Equal(0, vm.BrokenCount);
        Assert.Equal(0, vm.SelectedCount);
        Assert.True(vm.MoveToRecycleBin);
        Assert.Contains("Scan", vm.ScanStatus);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var vm = new ShortcutCleanerViewModel(new Services.ShortcutCleanerService());
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", IsSelected = false });
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "B", IsSelected = false });

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.BrokenShortcuts, s => Assert.True(s.IsSelected));
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var vm = new ShortcutCleanerViewModel(new Services.ShortcutCleanerService());
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

    // ── DeleteSelected confirmation gate (destructive — removes broken .lnk files) ──

    [Fact]
    public void DeleteSelected_WhenUserDeclinesConfirm_DeletesNothing()
    {
        var vm = new ShortcutCleanerViewModel(new ShortcutCleanerService());
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", ShortcutPath = @"C:\nope\a.lnk", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            vm.DeleteSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must leave the list untouched — nothing was deleted.
            Assert.Single(vm.BrokenShortcuts);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void DeleteSelected_WithNoSelection_NeverPromptsConfirm()
    {
        var vm = new ShortcutCleanerViewModel(new ShortcutCleanerService());
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", IsSelected = false });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.DeleteSelectedCommand.Execute(null);

            // No items selected → the destructive prompt must not appear at all.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
