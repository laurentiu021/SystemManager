// SysManager · FileLockViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FileLockViewModel"/>. The read-only / gating tests drive the real
/// <see cref="FileLockService"/> (its <c>FindLockers</c> is a read-only Restart Manager
/// query, safe against a temp file); the mutating-path test substitutes
/// <see cref="IFileLockService"/> so the confirmed <c>KillSelected</c> success path can be
/// executed (asserting <c>KillProcess</c> is called with the selected pid) without
/// terminating a real process. The confirmation-gated branches (critical-process block,
/// user-declines) and CanExecute gating are covered against the real service.
///
/// Serialized because several tests swap the global <see cref="DialogService.Instance"/> static.
/// </summary>
[Collection("ProcessWideStatics")]
public class FileLockViewModelTests
{
    private static FileLockViewModel NewVm() => new(new FileLockService());

    private static FileLockViewModel NewVm(IFileLockService service) => new(service);

    [Fact]
    public void Constructor_Succeeds_WithInitialState()
    {
        var vm = NewVm();
        Assert.Equal("", vm.Path);
        Assert.False(vm.HasScanned);
        Assert.Empty(vm.Lockers);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.KillSelectedCommand);
        Assert.NotNull(vm.BrowseCommand);
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }

    [Fact]
    public void Scan_RequiresNonEmptyPath()
    {
        var vm = NewVm();
        // CanScan == !IsBusy && Path is non-whitespace.
        Assert.False(vm.ScanCommand.CanExecute(null));

        vm.Path = @"C:\some\file.txt";
        Assert.True(vm.ScanCommand.CanExecute(null));

        vm.Path = "   ";
        Assert.False(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void Scan_DisabledWhileBusy()
    {
        var vm = NewVm();
        vm.Path = @"C:\some\file.txt";
        Assert.True(vm.ScanCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void Kill_RequiresSelectionAndNotBusy()
    {
        var vm = NewVm();
        // CanKill == !IsBusy && SelectedLocker is not null.
        Assert.Null(vm.SelectedLocker);
        Assert.False(vm.KillSelectedCommand.CanExecute(null));

        vm.SelectedLocker = new FileLocker(1234, "notepad.exe", "RmMainWindow", null);
        Assert.True(vm.KillSelectedCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.KillSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Scan_OnUnlockedTempFile_ReportsNoLockers()
    {
        // A freshly-created temp file we are not holding open has no Restart Manager lockers,
        // so the read-only scan should complete and report zero processes deterministically.
        string temp = Path.Combine(Path.GetTempPath(), "sysmgr_filelock_test_" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, "x");
        try
        {
            var vm = NewVm();
            vm.Path = temp;
            await vm.ScanCommand.ExecuteAsync(null);

            Assert.True(vm.HasScanned);
            Assert.False(vm.IsBusy);
            Assert.Empty(vm.Lockers);
            Assert.Contains("No process", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void KillSelected_OnCriticalProcess_InformsWithoutAsking_AndDoesNotKill()
    {
        // A critical (RmCritical) locker must be blocked: the VM states that it cannot be ended and
        // returns without attempting to terminate it. No kill is issued, so this exercises the guard
        // branch safely without touching a real process.
        //
        // Inform, not Confirm. This used to be a Yes/No dialog whose answer was discarded — the user
        // chose between two buttons that did the same thing — which is how people learn to click through
        // prompts without reading them, weakening the confirmations that DO gate something destructive.
        // The assertion is deliberately two-sided: the notice appears AND no question is asked.
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.SelectedLocker = new FileLocker(4, "System", "RmCritical", null);

            vm.KillSelectedCommand.Execute(null);

            dialog.Received(1).Inform(Arg.Any<string>(), Arg.Any<string>());
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void KillSelected_WhenUserDeclines_DoesNothing()
    {
        // Declining the confirm short-circuits before KillProcess, so no process is touched
        // and the status message is left untouched from construction.
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            string statusBefore = vm.StatusMessage;
            vm.SelectedLocker = new FileLocker(999999, "phantom.exe", "RmMainWindow", null);

            vm.KillSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Equal(statusBefore, vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void RelaunchAsAdmin_WithoutWpfApp_DoesNotThrow()
    {
        // In a test host Application.Current is null, so AdminHelper.RelaunchAsAdmin returns
        // false early and the command is a safe no-op (no shutdown, no elevation prompt).
        var vm = NewVm();
        var ex = Record.Exception(() => vm.RelaunchAsAdminCommand.Execute(null));
        Assert.Null(ex);
    }

    // ── Mutating-path test (substituted IFileLockService) ──────────────────

    [Fact]
    public async Task KillSelected_WhenConfirmed_CallsKillProcessWithPid_AndRescans()
    {
        // A non-critical locker, the user confirms, and the service reports a successful kill.
        // The VM must call KillProcess(pid) exactly once, then trigger the re-scan via the
        // substituted FindLockers (returning no lockers). No real process is touched.
        const int pid = 4242;
        var service = Substitute.For<IFileLockService>();
        service.KillProcess(pid).Returns(true);
        service.FindLockers(Arg.Any<string>())
            .Returns(new FileLockScan([], IsFolder: false, FilesChecked: 1, CheckedOnlyPart: false)); // post-kill re-scan

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm(service);
            vm.Path = @"C:\some\locked\file.txt"; // CanScan needs a non-empty path for the re-scan
            vm.SelectedLocker = new FileLocker(pid, "target.exe", "RmMainWindow", null);

            vm.KillSelectedCommand.Execute(null);

            service.Received(1).KillProcess(pid);
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());

            // KillSelected fires ScanCommand.Execute (async) to refresh the locker list; await it.
            await vm.ScanCommand.ExecuteAsync(null);
            service.Received().FindLockers(Arg.Any<string>());
            Assert.Empty(vm.Lockers);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── What a check reports (#2502) ───────────────────────────────────
    //
    // Every failure used to reach "No process is currently using that path.": a folder, which Restart Manager
    // refuses; a path that does not exist, which it accepts; and a failed check. That is the answer someone who
    // cannot delete a file acts on.

    private static FileLocker Holder() => new(4242, "editor.exe", "RmMainWindow", null);

    [Fact]
    public void DescribeScan_AFile()
    {
        Assert.Equal("No process is currently using that file.",
            FileLockViewModel.DescribeScan(new FileLockScan([], false, 1, false)));
        Assert.Equal("1 process(es) are using that file.",
            FileLockViewModel.DescribeScan(new FileLockScan([Holder()], false, 1, false)));
    }

    [Fact]
    public void DescribeScan_AFolder_SpeaksOfTheFilesInIt()
    {
        var none = FileLockViewModel.DescribeScan(new FileLockScan([], true, 12, false));
        Assert.StartsWith("No process is using any of the", none);
        Assert.Contains("files in that folder.", none);

        Assert.Equal("1 process(es) are using files in that folder.",
            FileLockViewModel.DescribeScan(new FileLockScan([Holder()], true, 12, false)));
    }

    [Fact]
    public void DescribeScan_AFolderTooLargeToCheckWhole_SaysHowMuchWasChecked()
    {
        var text = FileLockViewModel.DescribeScan(new FileLockScan([], true, 1000, CheckedOnlyPart: true));

        Assert.Contains("holds more files", text);
        Assert.Contains("were checked", text);
    }

    [Fact]
    public void DescribeScan_AFolderWithNothingToCheck_DoesNotClaimNothingIsUsingIt()
    {
        var text = FileLockViewModel.DescribeScan(new FileLockScan([], true, 0, false));

        Assert.Equal("SysManager found no files it could check in that folder.", text);
        Assert.DoesNotContain("No process", text);
    }

    [Fact]
    public async Task ACheckThatFailed_SaysSo_AndClearsTheList()
    {
        var service = Substitute.For<IFileLockService>();
        var checks = 0;
        service.FindLockers(Arg.Any<string>())
            .Returns(_ => ++checks == 1 ? new FileLockScan([Holder()], false, 1, false) : null);
        var vm = NewVm(service);
        vm.Path = @"C:\some\file.txt";
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Single(vm.Lockers);   // the premise: the first check listed a process

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(vm.Lockers);
        Assert.Contains("could not be completed", vm.StatusMessage);
        Assert.DoesNotContain("No process", vm.StatusMessage);
    }

    [Fact]
    public async Task APathThatDoesNotExist_SaysSo_NotThatNothingIsUsingIt()
    {
        var service = Substitute.For<IFileLockService>();
        service.FindLockers(Arg.Any<string>()).Returns(_ => throw new FileNotFoundException("No file or folder exists at that path."));
        var vm = NewVm(service);
        vm.Path = @"C:\some\flie.txt";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.StartsWith("No file or folder exists at that path.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }
}
