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
/// query, safe against a temp file). Every test that reaches <c>KillSelected</c> substitutes
/// <see cref="IFileLockService"/>, the ones that must not end anything as well as the ones
/// that do: a regression in a guard then shows up as a call to a substitute, never as a
/// real process ended.
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
            var service = Substitute.For<IFileLockService>();
            var vm = NewVm(service);
            vm.SelectedLocker = new FileLocker(4, "System", "RmCritical", null);

            vm.KillSelectedCommand.Execute(null);

            dialog.Received(1).Inform(Arg.Any<string>(), Arg.Any<string>());
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            service.DidNotReceiveWithAnyArgs().KillProcess(default, default);
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
            var service = Substitute.For<IFileLockService>();
            var vm = NewVm(service);
            string statusBefore = vm.StatusMessage;
            vm.SelectedLocker = new FileLocker(4242, "phantom.exe", "RmMainWindow", null);

            vm.KillSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            service.DidNotReceiveWithAnyArgs().KillProcess(default, default);
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

    // ── Mutating-path tests (substituted IFileLockService) ──────────────────
    //
    // End process passes the locker's start time with its ID. The list can be minutes old, and Windows gives a closed
    // process's ID to the next one started, so the ID alone ended whichever program had it by then (#2514).

    private const int Pid = 4242;
    private static readonly DateTime Started = new(2026, 9, 30, 8, 15, 42, DateTimeKind.Local);

    /// <summary>A substitute whose end-process call answers <paramref name="outcome"/> and whose re-scan finds nothing.</summary>
    private static IFileLockService ServiceThatAnswers(ProcessManagerService.KillOutcome outcome)
    {
        var service = Substitute.For<IFileLockService>();
        service.KillProcess(Arg.Any<int>(), Arg.Any<DateTime?>()).Returns(outcome);
        service.FindLockers(Arg.Any<string>())
            .Returns(new FileLockScan([], IsFolder: false, FilesChecked: 1, CheckedOnlyPart: false));
        return service;
    }

    /// <summary>A view model with the locker at <see cref="Pid"/>, started at <see cref="Started"/>, selected.</summary>
    private static FileLockViewModel VmWithTheLockerSelected(IFileLockService service)
    {
        var vm = NewVm(service);
        vm.Path = @"C:\some\locked\file.txt";
        vm.SelectedLocker = new FileLocker(Pid, "target.exe", "RmMainWindow", Started);
        return vm;
    }

    [Fact]
    public async Task KillSelected_WhenConfirmed_EndsTheListedProcess_AndRescans()
    {
        var service = ServiceThatAnswers(ProcessManagerService.KillOutcome.Ended);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = VmWithTheLockerSelected(service);

        await vm.KillSelectedCommand.ExecuteAsync(null);

        service.Received(1).KillProcess(Pid, Started);
        service.Received(1).FindLockers(Arg.Any<string>());
        Assert.Empty(vm.Lockers);
        Assert.Equal("Ended target.exe (4242). No process is currently using that file.", vm.StatusMessage);
    }

    [Fact]
    public async Task KillSelected_WhenTheListedProcessHadAlreadyClosed_SaysNothingWasEnded_AndRescans()
    {
        // NotRunning: nothing has the ID, or a process that started at another time has it now. Either way the row
        // is stale, so the list is checked again.
        var service = ServiceThatAnswers(ProcessManagerService.KillOutcome.NotRunning);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = VmWithTheLockerSelected(service);

        await vm.KillSelectedCommand.ExecuteAsync(null);

        service.Received(1).KillProcess(Pid, Started);
        service.Received(1).FindLockers(Arg.Any<string>());
        Assert.Equal(
            "target.exe (4242) had already closed, so nothing was ended. No process is currently using that file.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task KillSelected_WhenWindowsRefuses_SaysWhy_AndDoesNotRescan()
    {
        // The process is still running, so the row still names it. The message no longer offers "or it already
        // exited": that is its own outcome now.
        var service = ServiceThatAnswers(ProcessManagerService.KillOutcome.Refused);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = VmWithTheLockerSelected(service);

        await vm.KillSelectedCommand.ExecuteAsync(null);

        service.Received(1).KillProcess(Pid, Started);
        service.DidNotReceive().FindLockers(Arg.Any<string>());
        Assert.Equal("Couldn't end target.exe (4242) — it may need administrator rights.", vm.StatusMessage);
    }

    [Fact]
    public async Task KillSelected_ForALockerWithNoStartTime_PassesNone()
    {
        // Restart Manager's start time is null only when it gave an unusable one. The service then skips the check, as
        // Process Manager does for a process whose start time Windows would not give.
        var service = ServiceThatAnswers(ProcessManagerService.KillOutcome.Ended);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = NewVm(service);
        vm.Path = @"C:\some\locked\file.txt";
        vm.SelectedLocker = new FileLocker(Pid, "target.exe", "RmMainWindow", null);

        await vm.KillSelectedCommand.ExecuteAsync(null);

        service.Received(1).KillProcess(Pid, null);
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
