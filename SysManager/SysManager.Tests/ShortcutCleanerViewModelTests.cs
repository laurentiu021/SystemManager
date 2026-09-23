// SysManager · ShortcutCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

// Serialized: the DeleteSelected gate tests swap the static DialogService.Instance.
[Collection("ProcessWideStatics")]
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

    // The .lnk walk itself — depth, the "*.lnk" pattern and a missing root — is covered in
    // SafeFileWalkTests, because the scan now shares one walk with every other service that walks a tree
    // rather than keeping a private copy of it.

    // ── "broken" must mean confirmed gone, not unreachable (#2378) ──
    //
    // The two filesystem questions are injected, so every case below is deterministic with no unplugged
    // drive, no offline server and no privileges. That is the point of the seam: the defect was reachable
    // only through conditions a test cannot create, which is why it survived.

    private static FileAttributes Reachable(string _) => FileAttributes.Normal;
    private static bool Ready(string _) => true;

    [Fact]
    public void ClassifyTarget_TargetIsThere_IsPresent()
    {
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Present,
            ShortcutCleanerService.ClassifyTarget(@"C:\Program Files\App\app.exe", Reachable, Ready));
    }

    [Fact]
    public void ClassifyTarget_ReadableVolumeAndNoSuchFile_IsMissing()
    {
        // The only case that may be deleted: the volume answered, and the file was not on it.
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Missing,
            ShortcutCleanerService.ClassifyTarget(
                @"C:\Program Files\Gone\gone.exe",
                _ => throw new FileNotFoundException(),
                Ready));
    }

    [Fact]
    public void ClassifyTarget_DriveNotAttached_IsUnreachable_AndAsksTheFilesystemNothing()
    {
        // The common case: a shortcut to a file on a USB stick that is not plugged in. Asserting that
        // readAttributes is never called is half the test — an unmounted volume must be decided from the
        // root alone, because asking the OS about a path on it is both slow and ambiguous.
        var probed = false;

        var verdict = ShortcutCleanerService.ClassifyTarget(
            @"E:\Photos\holiday.jpg",
            _ => { probed = true; return FileAttributes.Normal; },
            _ => false);

        Assert.Equal(ShortcutCleanerService.TargetVerdict.Unreachable, verdict);
        Assert.False(probed, "an unattached volume was probed anyway, which is slow and cannot answer");
    }

    [Fact]
    public void ClassifyTarget_AccessDenied_IsUnreachable_NotMissing()
    {
        // File.Exists returns false for this too, which is how a live file in a folder the user cannot read
        // became a "broken shortcut". The tab is usable without elevation, so this is not a corner case.
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Unreachable,
            ShortcutCleanerService.ClassifyTarget(
                @"C:\Users\someone-else\Documents\theirs.docx",
                _ => throw new UnauthorizedAccessException(),
                Ready));
    }

    [Fact]
    public void ClassifyTarget_DeviceNotReady_IsUnreachable()
    {
        // A BitLocker volume waiting for its password, or a card reader with no card: the OS reports an
        // IOException rather than an absence, and the two must not be conflated.
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Unreachable,
            ShortcutCleanerService.ClassifyTarget(
                @"D:\Locked\file.bin",
                _ => throw new IOException("The device is not ready."),
                Ready));
    }

    [Fact]
    public void ClassifyTarget_UncTargetNotFound_IsUnreachable_NotMissing()
    {
        // The load-bearing one. An absent share and a sleeping NAS are indistinguishable from here without
        // waiting out a network timeout per shortcut — and "Recent Items" is a scanned location, so a machine
        // that has ever opened a file from a share has a list full of these. Refusing to judge costs a dead
        // network shortcut staying put; judging wrongly deletes a live one.
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Unreachable,
            ShortcutCleanerService.ClassifyTarget(
                @"\\nas\media\film.mkv",
                _ => throw new FileNotFoundException(),
                Ready));
    }

    [Fact]
    public void ClassifyTarget_UncTargetThatAnswers_IsStillPresent()
    {
        // The negative half: refusing to call a UNC target MISSING must not stop it being recognised as
        // there. A reachable share answers immediately, and a scan that reported every network shortcut as
        // undecided would put a permanent warning on a machine whose NAS is simply switched on.
        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Present,
            ShortcutCleanerService.ClassifyTarget(@"\\nas\media\film.mkv", Reachable, Ready));
    }

    [Fact]
    public void ClassifyTarget_AgainstTheRealFilesystem_StillCallsAMissingFileMissing()
    {
        // No seams: the defaults have to work, or every case above tests only the injected doubles. A path
        // under %TEMP% is on an attached volume this user can read, so "not there" is the honest answer.
        var absent = Path.Combine(Path.GetTempPath(), "smtest_no_such_target_" + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            ShortcutCleanerService.TargetVerdict.Missing,
            ShortcutCleanerService.ClassifyTarget(absent));
    }

    // ── the report has to say when it could not look ──

    [Fact]
    public void ScanReport_WithNothingUndecided_SaysNothing()
    {
        // The negative half: a clean scan must not grow a warning nobody needs.
        Assert.Null(new ShortcutScanReport().Notice);
    }

    [Theory]
    [InlineData(1, "1 shortcut points")]
    [InlineData(4, "4 shortcuts point")]
    public void ScanReport_WithUndecidedTargets_NamesTheCountAndThatTheyWereLeftAlone(int count, string expected)
    {
        var notice = new ShortcutScanReport { UnreachableTargets = count }.Notice;

        Assert.NotNull(notice);
        Assert.Contains(expected, notice, StringComparison.Ordinal);
        Assert.Contains("left alone", notice, StringComparison.Ordinal);

        // The reason, in the user's terms rather than the mechanism's: they need to know a drive being
        // unplugged is why, or the sentence reads as the app failing.
        Assert.Contains("not plugged in", notice, StringComparison.Ordinal);
    }
}
