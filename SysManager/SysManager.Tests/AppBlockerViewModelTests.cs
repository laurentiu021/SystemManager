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
[Collection("ProcessWideStatics")]
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
        Assert.False(app.IsSelected);
    }

    [Fact]
    public void BlockedApp_CarriesNoFieldItCannotFill()
    {
        // FullPath and BlockedAt were declared and could never hold a truthful value: an IFEO key records
        // the executable NAME and nothing else, so there is no path to report and no creation time to
        // read. BlockedAt was worse than empty — it was assigned DateTime.Now when the list was READ, so
        // it reported when the tab was opened, not when anything was blocked. The default-values test
        // above previously asserted FullPath's default, which made a permanently-empty field look
        // exercised.
        var names = typeof(BlockedApp).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("FullPath", names);
        Assert.DoesNotContain("BlockedAt", names);

        // Vacuity floor: the reflection must actually be seeing the model.
        Assert.Contains("ExecutableName", names);
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
            // Assert on both entry points: the view model calls TryBlockApp, and asserting only
            // the old BlockApp would leave this test passing while checking nothing.
            blocker.DidNotReceive().TryBlockApp(Arg.Any<string>());
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
        // The view model calls TryBlockApp now, so it can tell a safety refusal from a
        // permissions problem instead of reporting every failure as "check admin privileges".
        blocker.TryBlockApp(Arg.Any<string>()).Returns(AppBlockerService.BlockResult.Success);
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
            blocker.Received(1).TryBlockApp("game.exe");
            Assert.Contains("Blocked game.exe", vm.BlockStatus);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Theory]
    [InlineData(AppBlockerService.BlockResult.BootCritical, "required for Windows to start")]
    [InlineData(AppBlockerService.BlockResult.OwnExecutable, "SysManager itself")]
    [InlineData(AppBlockerService.BlockResult.ExternalDebuggerPresent, "already registered a debugger")]
    [InlineData(AppBlockerService.BlockResult.InvalidName, "not a valid executable name")]
    public void BlockApp_SafetyRefusal_DoesNotBlameAdminRights(
        AppBlockerService.BlockResult refusal, string expectedFragment)
    {
        // Each of these is SysManager deliberately declining. Reporting them as a permissions
        // problem sent the user to relaunch elevated, where the same guard refuses again.
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.TryBlockApp(Arg.Any<string>()).Returns(refusal);
        var vm = NewVm(blocker);
        vm.NewExeName = "something.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            Assert.Contains(expectedFragment, vm.BlockStatus);
            Assert.DoesNotContain("administrator", vm.BlockStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void BlockApp_AccessDenied_IsTheOnlyCaseThatMentionsAdminRights()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.TryBlockApp(Arg.Any<string>()).Returns(AppBlockerService.BlockResult.AccessDenied);
        var vm = NewVm(blocker);
        vm.NewExeName = "something.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            Assert.Contains("administrator", vm.BlockStatus, StringComparison.OrdinalIgnoreCase);
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
