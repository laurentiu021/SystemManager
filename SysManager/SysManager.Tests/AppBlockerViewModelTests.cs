// SysManager · AppBlockerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

// Serialized: the confirm-gate tests swap the static DialogService.Instance, which is
// process-wide shared state (see the DialogService test-collection used elsewhere).
[Collection("DialogService")]
public class AppBlockerViewModelTests
{
    // A blocker that reports nothing blocked — keeps the VM ctor's RefreshList()
    // a no-op so these tests exercise pure VM logic without registry access.
    private static AppBlockerViewModel NewVm()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.GetBlockedApps().Returns([]);
        return NewVm(blocker);
    }

    // The VM loads the blocked list asynchronously off the UI thread; wait for that init to
    // finish so the background load can't race a test that mutates BlockedApps afterwards.
    private static AppBlockerViewModel NewVm(IAppBlockerService blocker)
    {
        blocker.GetBlockedApps().Returns([]);
        var vm = new AppBlockerViewModel(blocker);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = NewVm();
        Assert.Equal("", vm.NewExeName);
        Assert.NotNull(vm.BlockedApps);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var vm = NewVm();
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "a.exe", IsSelected = false });
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "b.exe", IsSelected = false });

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.BlockedApps, a => Assert.True(a.IsSelected));
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var vm = NewVm();
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

    // ── Confirmation-gate tests (destructive ops must route through Confirm) ──

    [Fact]
    public void BlockApp_WhenUserDeclinesConfirm_DoesNotBlock()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        var vm = NewVm(blocker);
        vm.NewExeName = "game.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            blocker.DidNotReceive().BlockApp(Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void BlockApp_WhenUserConfirms_BlocksApp()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.BlockApp(Arg.Any<string>()).Returns(true);
        var vm = NewVm(blocker);
        vm.NewExeName = "game.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            blocker.Received(1).BlockApp("game.exe");
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void UnblockSelected_WhenUserDeclinesConfirm_DoesNotUnblock()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        var vm = NewVm(blocker);
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "game.exe", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.UnblockSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            blocker.DidNotReceive().UnblockApp(Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void UnblockSelected_WhenUserConfirms_UnblocksSelected()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.UnblockApp(Arg.Any<string>()).Returns(true);
        var vm = NewVm(blocker);
        vm.BlockedApps.Add(new BlockedApp { ExecutableName = "game.exe", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.UnblockSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            blocker.Received(1).UnblockApp("game.exe");
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
