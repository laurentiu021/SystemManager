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

    [Fact]
    public void DeleteSelected_WhenDiskLocked_DoesNotDelete()
    {
        var vm = new ShortcutCleanerViewModel(new ShortcutCleanerService());
        vm.BrokenShortcuts.Add(new BrokenShortcut { Name = "A", ShortcutPath = @"C:\nope\a.lnk", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;

        // Hold the Disk lock so the delete must bail rather than race a disk op.
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Test Holder");
        Assert.NotNull(held);
        try
        {
            vm.DeleteSelectedCommand.Execute(null);

            // Confirm was shown, but the lock was unavailable → nothing deleted.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Single(vm.BrokenShortcuts);
            Assert.Contains("already running", vm.ScanStatus);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── EnumerateLnkFilesSafe (regression: nested scan must not abort on one bad folder) ──

    [Fact]
    public void EnumerateLnkFilesSafe_FindsLnkFilesAtAllDepths()
    {
        // Regression: the old SearchOption.AllDirectories enumerator threw the first time it
        // hit an unreadable subfolder, aborting the whole location and dropping every later
        // shortcut. The tolerant walk must find .lnk files at every depth and ignore non-.lnk.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smtest_lnkscan_" + System.Guid.NewGuid().ToString("N"));
        var deep = System.IO.Path.Combine(root, "a", "b", "c");
        System.IO.Directory.CreateDirectory(deep);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "top.lnk"), "");
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "a", "mid.lnk"), "");
            System.IO.File.WriteAllText(System.IO.Path.Combine(deep, "deep.lnk"), "");
            System.IO.File.WriteAllText(System.IO.Path.Combine(deep, "not-a-shortcut.txt"), "");

            var found = ShortcutCleanerService.EnumerateLnkFilesSafe(root, CancellationToken.None)
                .Select(System.IO.Path.GetFileName)
                .OrderBy(n => n)
                .ToList();

            Assert.Equal(new[] { "deep.lnk", "mid.lnk", "top.lnk" }, found);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EnumerateLnkFilesSafe_MissingRoot_ReturnsEmptyWithoutThrowing()
    {
        // A root that doesn't exist must yield nothing rather than throw (the per-directory
        // try/catch swallows the access error).
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smtest_noroot_" + System.Guid.NewGuid().ToString("N"));
        var found = ShortcutCleanerService.EnumerateLnkFilesSafe(missing, CancellationToken.None).ToList();
        Assert.Empty(found);
    }
}
