// SysManager · NotificationBlockerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="NotificationBlockerViewModel"/>: sender population, search filter,
/// pending-change tracking (per-app and master), the confirm gate, partial-failure baseline
/// handling, and discard — all against a substituted service (no registry).
/// </summary>
[Collection("DialogService")]
public class NotificationBlockerViewModelTests
{
    private static NotificationApp App(string aumid, bool enabled = true, string? name = null) =>
        new() { Aumid = aumid, DisplayName = name ?? aumid, IsEnabled = enabled };

    // The VM loads its senders asynchronously off the UI thread; wait for that init so tests
    // observe the populated collections deterministically instead of racing the background load.
    private static NotificationBlockerViewModel NewVm(INotificationBlockerService svc)
    {
        var vm = new NotificationBlockerViewModel(svc);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    private static INotificationBlockerService NewService(params NotificationApp[] apps)
    {
        var svc = Substitute.For<INotificationBlockerService>();
        svc.GetApps().Returns(apps);
        svc.IsGlobalToastEnabled().Returns(true);
        svc.SetAppEnabled(Arg.Any<string>(), Arg.Any<bool>()).Returns(true);
        svc.SetGlobalToastEnabled(Arg.Any<bool>()).Returns(true);
        return svc;
    }

    [Fact]
    public void Constructor_PopulatesAppsAndMaster()
    {
        var vm = NewVm(NewService(App("a.app"), App("b.app", enabled: false)));

        Assert.Equal(2, vm.Apps.Count);
        Assert.Equal(2, vm.FilteredApps.Count);
        Assert.True(vm.MasterEnabled);
        Assert.Equal(0, vm.PendingChangeCount);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void SearchText_FiltersByDisplayNameAndAumid()
    {
        var vm = NewVm(NewService(
            App("com.squirrel.slack.slack", name: "Slack"),
            App("Chrome", name: "Google Chrome"),
            App("other.app", name: "Other")));

        vm.SearchText = "slack";
        Assert.Single(vm.FilteredApps);

        vm.SearchText = "chrome";
        Assert.Single(vm.FilteredApps);

        vm.SearchText = "";
        Assert.Equal(3, vm.FilteredApps.Count);
    }

    [Fact]
    public void TogglingApp_TracksPendingChange_AndTogglingBackClearsIt()
    {
        var vm = NewVm(NewService(App("a.app")));

        vm.Apps[0].IsEnabled = false;
        Assert.Equal(1, vm.PendingChangeCount);
        Assert.True(vm.HasPendingChanges);

        vm.Apps[0].IsEnabled = true;
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public void TogglingMaster_TracksPendingChange()
    {
        var vm = NewVm(NewService(App("a.app")));

        vm.MasterEnabled = false;
        Assert.Equal(1, vm.PendingChangeCount);

        vm.MasterEnabled = true;
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public void ApplyChanges_WhenUserDeclinesConfirm_WritesNothing()
    {
        var svc = NewService(App("a.app"));
        var vm = NewVm(svc);
        vm.Apps[0].IsEnabled = false;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplyChangesCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            svc.DidNotReceive().SetAppEnabled(Arg.Any<string>(), Arg.Any<bool>());
            svc.DidNotReceive().SetGlobalToastEnabled(Arg.Any<bool>());
            Assert.Equal(1, vm.PendingChangeCount); // still pending
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void ApplyChanges_WhenUserConfirms_WritesAppAndMaster_AndRebaselines()
    {
        var svc = NewService(App("a.app"));
        var vm = NewVm(svc);
        vm.Apps[0].IsEnabled = false;
        vm.MasterEnabled = false;
        Assert.Equal(2, vm.PendingChangeCount);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplyChangesCommand.Execute(null);

            svc.Received(1).SetAppEnabled("a.app", false);
            svc.Received(1).SetGlobalToastEnabled(false);
            Assert.Equal(0, vm.PendingChangeCount); // rebased — nothing pending
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void ApplyChanges_FailedWrite_StaysPending()
    {
        var svc = NewService(App("a.app"), App("b.app"));
        svc.SetAppEnabled("a.app", Arg.Any<bool>()).Returns(false); // this write is denied
        var vm = NewVm(svc);
        vm.Apps.First(a => a.Aumid == "a.app").IsEnabled = false;
        vm.Apps.First(a => a.Aumid == "b.app").IsEnabled = false;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplyChangesCommand.Execute(null);

            // b.app applied and rebased; a.app failed and must remain pending.
            Assert.Equal(1, vm.PendingChangeCount);
            Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void ApplyChanges_MutingMaster_WarnsAboutSilencingEverything()
    {
        var svc = NewService(App("a.app"));
        var vm = NewVm(svc);
        vm.MasterEnabled = false;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplyChangesCommand.Execute(null);

            dialog.Received(1).Confirm(
                Arg.Is<string>(m => m != null && m.Contains("ALL notifications")),
                Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void DiscardChanges_RestoresBaseline()
    {
        var vm = NewVm(NewService(App("a.app"), App("b.app", enabled: false)));
        vm.Apps[0].IsEnabled = false;
        vm.Apps[1].IsEnabled = true;
        vm.MasterEnabled = false;
        Assert.Equal(3, vm.PendingChangeCount);

        vm.DiscardChangesCommand.Execute(null);

        Assert.True(vm.Apps[0].IsEnabled);
        Assert.False(vm.Apps[1].IsEnabled);
        Assert.True(vm.MasterEnabled);
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public async Task Refresh_ReloadsFromService_AndClearsPending()
    {
        var svc = NewService(App("a.app"));
        var vm = NewVm(svc);
        vm.Apps[0].IsEnabled = false; // pending

        svc.GetApps().Returns([App("a.app"), App("new.app")]);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Apps.Count);
        Assert.Equal(0, vm.PendingChangeCount); // fresh baseline
    }

    [Fact]
    public void NotificationApp_ActivitySummary_CoversAllShapes()
    {
        var never = new NotificationApp { Aumid = "a", DisplayName = "a" };
        Assert.Equal("no recent activity", never.ActivitySummary);

        var countOnly = new NotificationApp { Aumid = "b", DisplayName = "b", RecentCount = 5 };
        Assert.Equal("5 recent", countOnly.ActivitySummary);

        var full = new NotificationApp
        {
            Aumid = "c",
            DisplayName = "c",
            RecentCount = 3,
            LastNotification = new DateTime(2026, 7, 20, 14, 30, 0),
        };
        Assert.StartsWith("3 recent · last ", full.ActivitySummary);
    }
}
