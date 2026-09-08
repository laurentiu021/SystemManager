// SysManager · AppBlockerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
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

    // Every test below forces elevation ON, and the view-model is built INSIDE the scope because it caches
    // IsElevated in its constructor. Without that, these tests asserted nothing about blocking: BlockApp
    // returns at `if (!IsElevated)` before it ever reaches Confirm, so on a non-elevated host all six failed
    // with "Blocking requires administrator privileges." while on CI — whose runner IS elevated — all six
    // passed. Neither run said whether a safety refusal is reported as a safety refusal (#2168). The same
    // trap AdminHelper.ForceElevation was added for one class over, in CleanupViewModelTests.

    [Fact]
    public void BlockApp_WhenUserDeclinesConfirm_DoesNotBlock()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var blocker = Substitute.For<IAppBlockerService>();
        var vm = NewVm(blocker);
        Assert.True(vm.IsElevated, "the scope must reach the view-model's constructor");
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
        using var elevated = AdminHelper.ForceElevation(true);
        var blocker = Substitute.For<IAppBlockerService>();
        // The view model calls TryBlockApp now, so it can tell a safety refusal from a
        // permissions problem instead of reporting every failure as "check admin privileges".
        blocker.TryBlockApp(Arg.Any<string>()).Returns(AppBlockerService.BlockResult.Success);
        var vm = NewVm(blocker);
        Assert.True(vm.IsElevated, "the scope must reach the view-model's constructor");
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
    [InlineData(AppBlockerService.BlockResult.BootCritical, "part of Windows that has to keep working")]
    [InlineData(AppBlockerService.BlockResult.OwnExecutable, "SysManager itself")]
    [InlineData(AppBlockerService.BlockResult.ExternalDebuggerPresent, "already registered a debugger")]
    [InlineData(AppBlockerService.BlockResult.InvalidName, "not a valid executable name")]
    public void BlockApp_SafetyRefusal_DoesNotBlameAdminRights(
        AppBlockerService.BlockResult refusal, string expectedFragment)
    {
        // Each of these is SysManager deliberately declining. Reporting them as a permissions
        // problem sent the user to relaunch elevated, where the same guard refuses again.
        using var elevated = AdminHelper.ForceElevation(true);
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.TryBlockApp(Arg.Any<string>()).Returns(refusal);
        var vm = NewVm(blocker);
        Assert.True(vm.IsElevated, "the scope must reach the view-model's constructor");
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

    /// <summary>
    /// <c>AccessDenied</c> from the service is the one refusal that SHOULD talk about administrator
    /// rights — and it must get there through the service, not through the view-model's own gate.
    /// </summary>
    /// <remarks>
    /// This was the worst of the seven (#2168), because it PASSED on a non-elevated host for the wrong
    /// reason: the elevation gate's own message also contains "administrator", so the assertion was
    /// satisfied by a code path that never called <c>TryBlockApp</c> at all. A false pass hides better than
    /// a false failure. Forcing elevation on and asserting the service was actually reached is what makes
    /// the assertion mean what its name says.
    /// </remarks>
    [Fact]
    public void BlockApp_AccessDenied_IsTheOnlyCaseThatMentionsAdminRights()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.TryBlockApp(Arg.Any<string>()).Returns(AppBlockerService.BlockResult.AccessDenied);
        var vm = NewVm(blocker);
        Assert.True(vm.IsElevated, "the scope must reach the view-model's constructor");
        vm.NewExeName = "something.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            blocker.Received(1).TryBlockApp("something.exe");
            Assert.Contains("administrator", vm.BlockStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    /// <summary>
    /// Without elevation, Block refuses before it touches anything: it says why, it does not ask the user
    /// to confirm something it cannot do, and it never reaches the service.
    /// </summary>
    /// <remarks>
    /// The branch a user without admin rights actually sees, and it was asserted nowhere for any of the ten
    /// view models that have one (#2168, swept in #2171). Every existing test here drove the ELEVATED path,
    /// so the gate could have been deleted, moved below the Confirm, or given a misleading message without
    /// a single failure — on CI, whose runner is elevated, or on a developer machine, where the six tests
    /// that did notice failed for a reason that looked like a broken parser.
    /// <para>Asserting the absence matters as much as the message: <c>Confirm</c> must NOT be reached. A gate
    /// that runs after the confirmation dialog would still produce the right status text while asking the
    /// user to approve an operation that then refuses itself — which is the shape of the Unblock path's own
    /// problem, filed separately.</para>
    /// </remarks>
    [Fact]
    public void BlockApp_WhenNotElevated_SaysWhyAndNeverReachesTheService()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var blocker = Substitute.For<IAppBlockerService>();
        var vm = NewVm(blocker);
        Assert.False(vm.IsElevated, "the scope must reach the view-model's constructor");
        vm.NewExeName = "game.exe";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // would say yes if asked
        DialogService.Instance = dialog;
        try
        {
            vm.BlockAppCommand.Execute(null);

            Assert.Contains("administrator", vm.BlockStatus, StringComparison.OrdinalIgnoreCase);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            blocker.DidNotReceive().TryBlockApp(Arg.Any<string>());
            blocker.DidNotReceive().BlockApp(Arg.Any<string>());
            Assert.Equal("game.exe", vm.NewExeName); // the typed name survives, so a relaunch can use it
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
