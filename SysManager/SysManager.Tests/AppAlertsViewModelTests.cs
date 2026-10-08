// SysManager · AppAlertsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

// ClearHistory's confirmation gate swaps the global DialogService.Instance, so this class must
// run in the serialized collection — otherwise a parallel class's substitute steals the Confirm
// call and the gate assertions go flaky.
[Collection("ProcessWideStatics")]
public class AppAlertsViewModelTests
{
    // Nothing kept yet, and nowhere real to keep it: every test starts from an empty history it can inspect.
    private static IAppAlertHistory NoHistory()
    {
        var history = Substitute.For<IAppAlertHistory>();
        history.Load().Returns(new AppAlertsRead([], Readable: true));
        history.Clear().Returns(true);
        return history;
    }

    private static AppAlertsViewModel NewVm(IAppAlertHistory? history = null) =>
        new(new Services.AppAlertService(), history ?? NoHistory());

    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = NewVm();
        Assert.False(vm.IsMonitoring);
        Assert.Equal(0, vm.AlertCount);
        Assert.Equal(0, vm.UnacknowledgedCount);
        Assert.Contains("Start", vm.MonitorStatus);
    }

    [Fact]
    public void AcknowledgeAll_SetsAllAcknowledged()
    {
        var vm = NewVm();
        vm.Alerts.Add(new AppInstallEntry { Name = "App1", IsAcknowledged = false });
        vm.Alerts.Add(new AppInstallEntry { Name = "App2", IsAcknowledged = false });

        vm.AcknowledgeAllCommand.Execute(null);

        Assert.All(vm.Alerts, a => Assert.True(a.IsAcknowledged));
        Assert.Equal(0, vm.UnacknowledgedCount);
    }

    [Fact]
    public void ClearHistory_WhenConfirmed_RemovesAllAlerts()
    {
        var vm = NewVm();
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });
        vm.Alerts.Add(new AppInstallEntry { Name = "App2" });
        vm.AlertCount = vm.Alerts.Count;   // the guard reads AlertCount, so it must be real here

        using var _ = new DialogAnswer(true);
        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Alerts);
        Assert.Equal(0, vm.AlertCount);
    }

    [Fact]
    public void ClearHistory_WhenDeclined_KeepsTheHistory()
    {
        // This list is the only record of what installed itself. Answering "No" must leave it
        // completely untouched, on screen and in the copy kept on disk.
        var history = NoHistory();
        var vm = NewVm(history);
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });
        vm.Alerts.Add(new AppInstallEntry { Name = "App2" });
        vm.AlertCount = vm.Alerts.Count;
        vm.UnacknowledgedCount = 2;

        using var _ = new DialogAnswer(false);
        vm.ClearHistoryCommand.Execute(null);

        Assert.Equal(2, vm.Alerts.Count);
        Assert.Equal(2, vm.AlertCount);
        Assert.Equal(2, vm.UnacknowledgedCount);
        history.DidNotReceive().Clear();
    }

    [Fact]
    public void ClearHistory_WhenConfirmed_ClearsTheKeptCopyToo()
    {
        var history = NoHistory();
        var vm = NewVm(history);
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });
        vm.AlertCount = 1;

        using var _ = new DialogAnswer(true);
        vm.ClearHistoryCommand.Execute(null);

        history.Received(1).Clear();
        Assert.Equal("History cleared.", vm.MonitorStatus);
    }

    [Fact]
    public void ClearHistory_AsksInWordsThatSayRecentChangesLosesThemToo()
    {
        // The question used to say the history was not saved to disk. It is now, and Recent Changes lists it.
        var vm = NewVm();
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });
        vm.AlertCount = 1;

        using var answer = new DialogAnswer(false);
        vm.ClearHistoryCommand.Execute(null);

        var asked = Assert.Single(answer.Messages);
        Assert.Contains("Recent Changes", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("not saved", asked, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearHistory_WhenTheKeptCopyCannotBeRemoved_SaysItComesBack()
    {
        var history = NoHistory();
        history.Clear().Returns(false);
        var vm = NewVm(history);

        using var _ = new DialogAnswer(true);
        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Alerts);
        Assert.Contains("could not be removed", vm.MonitorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_ListsWhatEarlierSessionsNoticed()
    {
        // #1507: the list lived only in memory, so closing SysManager lost every detection.
        var history = NoHistory();
        history.Load().Returns(new AppAlertsRead(
            [new AppInstallEntry { Name = "Newer" }, new AppInstallEntry { Name = "Older", IsAcknowledged = true }],
            Readable: true));

        using var vm = NewVm(history);

        Assert.Equal(["Newer", "Older"], vm.Alerts.Select(a => a.Name));
        Assert.Equal(2, vm.AlertCount);
        Assert.Equal(1, vm.UnacknowledgedCount);
        Assert.Equal("2 installations noticed earlier are listed below. Click Start to watch for new ones.", vm.MonitorStatus);
        Assert.Null(vm.InitializationFault);
    }

    [Fact]
    public void Opening_WithOneKept_SaysOne()
    {
        var history = NoHistory();
        history.Load().Returns(new AppAlertsRead([new AppInstallEntry { Name = "Only" }], Readable: true));

        using var vm = NewVm(history);

        Assert.Equal("1 installation noticed earlier is listed below. Click Start to watch for new ones.", vm.MonitorStatus);
    }

    [Fact]
    public void Opening_WhenTheKeptListCannotBeRead_SaysSo()
    {
        var history = NoHistory();
        history.Load().Returns(AppAlertsRead.Unreadable);

        using var vm = NewVm(history);

        Assert.Empty(vm.Alerts);
        Assert.Equal(0, vm.AlertCount);
        Assert.Contains("could not be read", vm.MonitorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ADetection_IsListedAndKept()
    {
        var history = NoHistory();
        using var vm = NewVm(history);
        var entry = new AppInstallEntry { Name = "Search Pro Toolbar", Source = "Registry" };

        vm.Record(entry);

        history.Received(1).Add(entry);
        Assert.Same(entry, Assert.Single(vm.Alerts));
        Assert.Equal(1, vm.AlertCount);
        Assert.Equal(1, vm.UnacknowledgedCount);
        Assert.Equal("New app detected: Search Pro Toolbar", vm.MonitorStatus);
    }

    [Fact]
    public void ADetection_GoesAboveWhatWasKept()
    {
        var history = NoHistory();
        history.Load().Returns(new AppAlertsRead([new AppInstallEntry { Name = "Earlier" }], Readable: true));
        using var vm = NewVm(history);

        vm.Record(new AppInstallEntry { Name = "Now" });

        Assert.Equal(["Now", "Earlier"], vm.Alerts.Select(a => a.Name));
        Assert.Equal(2, vm.AlertCount);
    }

    [Fact]
    public void AcknowledgeAll_IsKept()
    {
        var history = NoHistory();
        var vm = NewVm(history);
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });

        vm.AcknowledgeAllCommand.Execute(null);

        history.Received(1).AcknowledgeAll();
    }

    [Fact]
    public void ClearHistory_WithNothingToLose_DoesNotPrompt()
    {
        // An empty list has nothing to confirm; prompting there would be pure noise. Answer
        // "No" and assert the clear still ran — proving no dialog gated it.
        var vm = NewVm();

        using var answer = new DialogAnswer(false);
        vm.ClearHistoryCommand.Execute(null);

        Assert.Equal(0, answer.Calls);
        Assert.Contains("cleared", vm.MonitorStatus);
    }

    [Fact]
    public async Task F5_NeverReplacesTheAlertHistory()
    {
        // Regression: F5 ran "Show Installed", which swapped the recorded detections for every program on
        // the PC, stamped each with the time of the keypress and asked nothing — while Clear History, the
        // deliberate way to lose the same list, asks first because the list is the only record of what
        // installed itself. F5 now checks for new installs, which can only add to the list.
        using var vm = NewVm();
        await vm.StartMonitoringCommand.ExecuteAsync(null);
        var detected = new AppInstallEntry { Name = "Detected earlier", Source = "Registry" };
        vm.Alerts.Add(detected);
        vm.AlertCount = 1;
        vm.UnacknowledgedCount = 1;

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5).ExecuteAsync(null);

        Assert.Same(detected, Assert.Single(vm.Alerts));
        Assert.Equal(1, vm.AlertCount);
        Assert.Equal(1, vm.UnacknowledgedCount);
        Assert.False(detected.IsAcknowledged);
    }

    [Fact]
    public void F5_BeforeMonitoringStarts_HasNothingToRun()
    {
        // A check for new installs compares against the list taken when monitoring started. Before that
        // there is no list, so there is nothing for F5 to do.
        using var vm = NewVm();

        var f5 = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5);
        Assert.False(f5.CanExecute(null));
    }

    [Fact]
    public async Task F5_AfterMonitoringStops_HasNothingToRun()
    {
        using var vm = NewVm();
        await vm.StartMonitoringCommand.ExecuteAsync(null);
        vm.StopMonitoringCommand.Execute(null);

        var f5 = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5);
        Assert.False(f5.CanExecute(null));
    }

    [Fact]
    public async Task F5_WhileMonitoring_ChecksAndKeepsMonitoring()
    {
        // Carries the idx-235 regression over to the new F5: it must not switch off the busy/monitoring
        // affordance, and it says what it did.
        using var vm = NewVm();
        await vm.StartMonitoringCommand.ExecuteAsync(null);

        var f5 = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5);
        Assert.True(f5.CanExecute(null));
        await f5.ExecuteAsync(null);

        Assert.True(vm.IsMonitoring);
        Assert.True(vm.IsBusy);
        Assert.Contains("Checked", vm.MonitorStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartMonitoring_IsAsync_AndSetsMonitoringState()
    {
        // Regression (P2 #42): StartMonitoring was a synchronous [RelayCommand] that ran the
        // full baseline scan (Program Files walk + double HKLM Uninstall enumeration) on the
        // UI thread, freezing the window. It is now an async command that offloads the scan.
        // The generated command must expose IAsyncRelayCommand and, once awaited, leave the
        // VM monitoring.
        using var vm = NewVm();
        Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.StartMonitoringCommand);

        await vm.StartMonitoringCommand.ExecuteAsync(null);

        Assert.True(vm.IsMonitoring);
        Assert.True(vm.IsBusy);

        vm.StopMonitoringCommand.Execute(null); // clean up watchers/timer
    }

    [Fact]
    public void AppInstallEntry_DefaultValues()
    {
        var entry = new AppInstallEntry();
        Assert.Equal("", entry.Name);
        Assert.Equal("", entry.Publisher);
        Assert.Equal("", entry.InstallPath);
        Assert.Equal("", entry.Source);
        Assert.False(entry.IsAcknowledged);
    }

    [Fact]
    public void AppInstallEntry_PropertyChanged_Fires()
    {
        var entry = new AppInstallEntry();
        string? changed = null;
        entry.PropertyChanged += (_, e) => changed = e.PropertyName;

        entry.Name = "TestApp";
        Assert.Equal("Name", changed);

        entry.IsAcknowledged = true;
        Assert.Equal("IsAcknowledged", changed);
    }
}
