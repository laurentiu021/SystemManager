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
/// Tests for <see cref="FileLockViewModel"/>. <see cref="FileLockService"/> is sealed with
/// no interface, so it cannot be substituted; these drive the real service. Its
/// <c>FindLockers</c> is a read-only Restart Manager query (safe to run against a temp file),
/// but <c>KillProcess</c> would terminate a real process, so the success path of
/// <c>KillSelected</c> is NOT executed — only the confirmation-gated branches (critical-process
/// block and user-declines) and CanExecute gating are covered.
///
/// Serialized because the critical-process and decline tests swap the global
/// <see cref="DialogService.Instance"/> static.
/// </summary>
[Collection("DialogService")]
public class FileLockViewModelTests
{
    private static FileLockViewModel NewVm() => new(new FileLockService());

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
    public void KillSelected_OnCriticalProcess_ShowsBlockDialog_AndDoesNotKill()
    {
        // A critical (RmCritical) locker must be blocked: the VM shows a single informational
        // confirm and returns without ever attempting to terminate it. No kill is issued, so
        // this exercises the guard branch safely without touching a real process.
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.SelectedLocker = new FileLocker(4, "System", "RmCritical", null);

            vm.KillSelectedCommand.Execute(null);

            // Exactly one (informational) confirm for the "cannot end" message; nothing else.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
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
}
