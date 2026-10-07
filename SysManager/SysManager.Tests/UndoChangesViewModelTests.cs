// SysManager · UndoChangesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UndoChangesViewModel"/> (#1525): what the tab shows, that it asks before every put-back with the
/// change's own question, and that it looks again — after a put-back and whenever it is shown.
/// </summary>
// Serialized: swaps DialogService.Instance, ActivityLogService.Instance and the elevation probe. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public sealed class UndoChangesViewModelTests
{
    private static UndoChange Change(UndoChangeKind kind = UndoChangeKind.PerformanceMode, bool needsAdmin = false,
        string? opensTab = null) =>
        new(kind, kind.ToString(), $"{kind} detail", "First changed 5 Oct 2026, 19:40", needsAdmin, "Put back…",
            ["Power plan: Ultimate Performance → Balanced"], $"Put back {kind}?", $"Put back {kind} — Confirm", opensTab);

    private static UndoScan Scan(params UndoChange[] changes) => new(changes, []);

    private static readonly RestorePoint Newest = new(9, "SysManager Privacy & Telemetry", new DateTime(2026, 10, 5, 19, 39, 0),
        "MODIFY_SETTINGS", "BEGIN_SYSTEM_CHANGE");

    private static IUndoChangesService Service(params UndoScan[] scans)
    {
        var service = Substitute.For<IUndoChangesService>();
        var answers = new Queue<UndoScan>(scans.Length == 0 ? [Scan()] : scans);
        service.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek()));
        service.LookForRestorePointAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RestorePointLook(Newest, Listed: true)));
        return service;
    }

    private static (UndoChangesViewModel Vm, IUndoChangesService Service, INavigationService Navigation) NewVm(
        params UndoScan[] scans)
    {
        var service = Service(scans);
        var navigation = Substitute.For<INavigationService>();
        var vm = new UndoChangesViewModel(service, navigation);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return (vm, service, navigation);
    }

    // ── What it shows ───────────────────────────────────────────────────────

    [Fact]
    public void TheTab_ShowsWhatItFound_AndCountsIt()
    {
        using var standard = AdminHelper.ForceElevation(false);
        var (vm, _, _) = NewVm(Scan(Change(), Change(UndoChangeKind.HostsFile, needsAdmin: true)));

        Assert.Equal(2, vm.Changes.Count);
        Assert.True(vm.HasChanges);
        Assert.False(vm.ShowNothingToPutBack);
        Assert.Equal("2 changes can be put back.", vm.StatusMessage);
        Assert.True(vm.Changes[0].CanAct);
        Assert.False(vm.Changes[1].CanAct);
        Assert.Equal("Windows shows the list of restore points only to an administrator.", vm.RestorePointText);
    }

    [Fact]
    public async Task NothingToPutBack_IsSaidOnlyOnceTheTabHasLooked()
    {
        var service = Service();
        var pending = new TaskCompletionSource<UndoScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ScanAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());

        Assert.False(vm.ShowNothingToPutBack);

        pending.SetResult(Scan());
        await vm.InitializationComplete;

        Assert.True(vm.ShowNothingToPutBack);
        Assert.Equal("Nothing to put back.", vm.StatusMessage);
        Assert.Equal("Nothing to put back", vm.EmptyTitle);
        Assert.Equal("Nothing SysManager changed is waiting to be put back. The sections below still apply.", vm.EmptyMessage);
    }

    [Fact]
    public void ACopyThatCouldNotBeUsed_IsNamed_AndNeverCalledNothing()
    {
        var (vm, _, _) = NewVm(new UndoScan([], [new UndoProblem(UndoChangeKind.Services, UndoProblemKind.Unreadable)]));

        // Not "Nothing to put back": the tab could not see everything.
        Assert.Equal("SysManager could not read what it kept for the services just now; look again in a moment.",
            vm.StatusMessage);
        Assert.Equal("Nothing found to put back", vm.EmptyTitle);
        Assert.Equal(vm.StatusMessage, vm.EmptyMessage);
    }

    [Fact]
    public void TheStatusLine_NamesEachCopyThatCouldNotBeUsed_AndWhy()
    {
        var scan = new UndoScan([Change()],
        [
            new UndoProblem(UndoChangeKind.Services, UndoProblemKind.Unreadable),
            new UndoProblem(UndoChangeKind.GamingProfile, UndoProblemKind.Unreadable),
            new UndoProblem(UndoChangeKind.HostsFile, UndoProblemKind.CannotCompare),
            new UndoProblem(UndoChangeKind.PerformanceMode, UndoProblemKind.Damaged),
        ]);

        // A copy that was read and had nothing to be compared with is not one SysManager "could not read".
        Assert.Equal("1 change can be put back. SysManager could not read what it kept for the services and game mode just "
            + "now; look again in a moment. SysManager could not compare the hosts file with what it kept just now; look "
            + "again in a moment. What SysManager kept for Performance Mode is damaged, so it cannot be put back.",
            UndoChangesViewModel.Summarize(scan));
    }

    [Fact]
    public void TheStatusLine_SaysPerformanceModeWaitsForGameMode()
    {
        var scan = new UndoScan([Change(UndoChangeKind.GamingProfile)], [], PerformanceWaitsForGameMode: true);

        Assert.Equal("1 change can be put back. Performance Mode can be put back once game mode is off.",
            UndoChangesViewModel.Summarize(scan));
    }

    [Fact]
    public void TheRestorePointLine_SaysWhatWindowsAnswered()
    {
        Assert.Equal("Newest: \"SysManager Privacy & Telemetry\", 5 Oct 2026, 19:39",
            UndoChangesViewModel.DescribeRestorePoint(new RestorePointLook(Newest, true), isElevated: true));
        Assert.Equal("Windows lists no restore points on this PC.",
            UndoChangesViewModel.DescribeRestorePoint(new RestorePointLook(null, true), isElevated: true));
        Assert.Equal("Windows would not list the restore points just now.",
            UndoChangesViewModel.DescribeRestorePoint(new RestorePointLook(null, false), isElevated: true));
        Assert.Equal("Windows shows the list of restore points only to an administrator.",
            UndoChangesViewModel.DescribeRestorePoint(new RestorePointLook(Newest, true), isElevated: false));
    }

    [Fact]
    public async Task TheRestorePoint_IsLookedFor_OnlyOnceTheTabIsShown()
    {
        // Asking costs a PowerShell session, and a tab nobody opens has no card to fill.
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, service, _) = NewVm();

        await service.DidNotReceive().LookForRestorePointAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Looking for the newest restore point…", vm.RestorePointText);

        vm.IsActive = true;
        await vm.RestorePointLookup;

        await service.Received(1).LookForRestorePointAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Newest: \"SysManager Privacy & Telemetry\", 5 Oct 2026, 19:39", vm.RestorePointText);
    }

    [Fact]
    public async Task TheChanges_DoNotWaitForTheRestorePoint()
    {
        // Creating a restore point holds the PowerShell runner, and the look for one waits behind it.
        using var elevated = AdminHelper.ForceElevation(true);
        var service = Service(Scan(), Scan(Change()));
        var never = new TaskCompletionSource<RestorePointLook>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LookForRestorePointAsync(Arg.Any<CancellationToken>()).Returns(never.Task);
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());
        await vm.InitializationComplete;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Changes);
        Assert.False(vm.IsBusy);
        Assert.Equal("Looking for the newest restore point…", vm.RestorePointText);
        never.SetResult(new RestorePointLook(null, Listed: true));
        await vm.RestorePointLookup;
        Assert.Equal("Windows lists no restore points on this PC.", vm.RestorePointText);
    }

    [Fact]
    public async Task AStandardUser_IsToldWithoutWindowsBeingAsked()
    {
        using var standard = AdminHelper.ForceElevation(false);
        var (vm, service, _) = NewVm();

        vm.IsActive = true;
        await vm.RestorePointLookup;

        await service.DidNotReceive().LookForRestorePointAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Windows shows the list of restore points only to an administrator.", vm.RestorePointText);
    }

    // ── Putting one back ────────────────────────────────────────────────────

    [Fact]
    public async Task PuttingBack_AsksTheChangesOwnQuestion_AndChangesNothingWhenDeclined()
    {
        var (vm, service, _) = NewVm(Scan(Change()));
        using var dialog = new DialogAnswer(confirm: false);

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        Assert.Equal("Put back PerformanceMode — Confirm\nPut back PerformanceMode?", Assert.Single(dialog.Messages));
        await service.DidNotReceive().PutBackAsync(Arg.Any<UndoChange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PuttingBack_WhenConfirmed_PutsBack_LooksAgain_SaysHowItWent_AndLogsIt()
    {
        var change = Change();
        var (vm, service, _) = NewVm(Scan(change), Scan());
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.PutBack, "Performance Mode is put back: your original settings are on again."));
        using var dialog = new DialogAnswer(confirm: true);
        using var log = new ActivityLogScope();

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        await service.Received(1).PutBackAsync(change, Arg.Any<CancellationToken>());
        await service.Received(2).ScanAsync(Arg.Any<CancellationToken>());
        Assert.Empty(vm.Changes);
        Assert.Equal("Performance Mode is put back: your original settings are on again.", vm.StatusMessage);
        var entry = Assert.Single(ActivityLogService.Instance.GetRecent(10));
        Assert.Equal("Undo Changes", entry.Action);
        Assert.Equal("Put Performance Mode's original settings back", entry.Detail);
    }

    [Fact]
    public async Task APutBackThatChangedPart_IsLogged()
    {
        var change = Change(UndoChangeKind.Services);
        var (vm, service, _) = NewVm(Scan(change));
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.PartlyPutBack, "1 of 2 services is back on."));
        using var dialog = new DialogAnswer(confirm: true);
        using var log = new ActivityLogScope();

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        Assert.Equal("Turned services back on", Assert.Single(ActivityLogService.Instance.GetRecent(10)).Detail);
    }

    [Fact]
    public async Task APutBackThatChangedNothing_IsNotLogged()
    {
        var change = Change();
        var (vm, service, _) = NewVm(Scan(change));
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.NothingLeft, "Nothing is left to put back for Performance Mode."));
        using var dialog = new DialogAnswer(confirm: true);
        using var log = new ActivityLogScope();

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        Assert.Empty(ActivityLogService.Instance.GetRecent(10));
        Assert.Equal("Nothing is left to put back for Performance Mode.", vm.StatusMessage);
    }

    [Fact]
    public async Task WhatHappenedToTheChange_IsSaid_EvenWhenTheLookAfterItFails()
    {
        var change = Change();
        var (vm, service, _) = NewVm(Scan(change));
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.PutBack, "Performance Mode is put back: your original settings are on again."));
        service.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UndoScan>(new InvalidOperationException("The ledger could not be read.")));
        using var dialog = new DialogAnswer(confirm: true);

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        Assert.Equal("Performance Mode is put back: your original settings are on again. The list could not be read again "
            + "just now; look again in a moment.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task WhatHappenedToTheChange_IsSaid_EvenWhenTheLookAfterItThrowsSomethingUnforeseen()
    {
        // Not one the tab catches, so it goes on up — and the line still says what happened to the change.
        var change = Change();
        var (vm, service, _) = NewVm(Scan(change));
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.PutBack, "Performance Mode is put back: your original settings are on again."));
        service.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UndoScan>(new NotSupportedException("The look is not supported here.")));
        using var dialog = new DialogAnswer(confirm: true);

        await Assert.ThrowsAsync<NotSupportedException>(() => vm.PutBackCommand.ExecuteAsync(vm.Changes[0]));

        Assert.Equal("Performance Mode is put back: your original settings are on again.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task APutBackThatThrows_SaysItFailed_RatherThanStayingOnItsProgressLine()
    {
        var change = Change();
        var (vm, service, _) = NewVm(Scan(change));
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UndoOutcome>(new InvalidOperationException("The record could not be opened.")));
        using var dialog = new DialogAnswer(confirm: true);
        using var log = new ActivityLogScope();

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        Assert.Equal("Putting back Performance Mode failed: The record could not be opened.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(ActivityLogService.Instance.GetRecent(10));
    }

    [Fact]
    public async Task ALookThatThrows_SaysSo_RatherThanStayingOnLooking()
    {
        // Each copy's own failures are caught where it is read, so this is the unexpected one: said plainly, with the
        // empty state, rather than "Looking for changes" over nothing.
        var service = Service();
        service.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UndoScan>(new InvalidOperationException("The ledger could not be read.")));
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());

        await vm.InitializationComplete;

        const string said = "SysManager could not look for changes just now: The ledger could not be read. Look again in a moment.";
        Assert.Equal(said, vm.StatusMessage);
        Assert.True(vm.ShowNothingToPutBack);
        Assert.Equal("Could not look for changes", vm.EmptyTitle);
        Assert.Equal(said, vm.EmptyMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task APutBack_ConfirmedWhileALookStartedUnderItsQuestion_WaitsForThatLook()
    {
        // The question is modal, and showing the window again after minimising it makes the tab active, which looks
        // again. The put-back must not run beside that look: its end would turn Busy off mid-put-back.
        var change = Change();
        var service = Service(Scan(change));
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());
        await vm.InitializationComplete;
        var row = vm.Changes[0];
        var look = new TaskCompletionSource<UndoScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PutBackAsync(change, Arg.Any<CancellationToken>())
            .Returns(new UndoOutcome(UndoOutcomeKind.PutBack, "Performance Mode is put back: your original settings are on again."));

        var previous = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
        {
            service.ScanAsync(Arg.Any<CancellationToken>()).Returns(look.Task, Task.FromResult(Scan()));
            vm.IsActive = true;
            return true;
        });
        DialogService.Instance = dialog;
        try
        {
            var putBack = vm.PutBackCommand.ExecuteAsync(row);

            Assert.True(vm.IsBusy);
            await service.DidNotReceive().PutBackAsync(Arg.Any<UndoChange>(), Arg.Any<CancellationToken>());

            look.SetResult(Scan(change));
            await putBack;

            await service.Received(1).PutBackAsync(change, Arg.Any<CancellationToken>());
            Assert.False(vm.IsBusy);
            Assert.Equal("Performance Mode is put back: your original settings are on again.", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = previous;
        }
    }

    [Fact]
    public async Task ARowThatNeedsAdministratorRights_CannotBePressed_WithoutThem()
    {
        using var standard = AdminHelper.ForceElevation(false);
        var (vm, service, _) = NewVm(Scan(Change(UndoChangeKind.Services, needsAdmin: true)));
        var row = vm.Changes[0];
        using var dialog = new DialogAnswer(confirm: true);

        Assert.False(vm.PutBackCommand.CanExecute(row));
        Assert.Equal("Needs administrator rights. Use Run as administrator at the top of the page.", row.ButtonHint);

        await vm.PutBackCommand.ExecuteAsync(row);

        Assert.Equal(0, dialog.Calls);
        await service.DidNotReceive().PutBackAsync(Arg.Any<UndoChange>(), Arg.Any<CancellationToken>());
        Assert.Equal("Putting back the services needs administrator rights. Use Run as administrator at the top of the page.",
            vm.StatusMessage);
    }

    [Fact]
    public void ARowThatNeedsAdministratorRights_CanBePressed_AsAdministrator()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, _, _) = NewVm(Scan(Change(UndoChangeKind.Services, needsAdmin: true)));

        Assert.True(vm.Changes[0].CanAct);
        Assert.True(vm.PutBackCommand.CanExecute(vm.Changes[0]));
        Assert.Equal("Asks first, and says what it will change.", vm.Changes[0].ButtonHint);
    }

    [Fact]
    public async Task TheSettingsWatchdogRow_OpensItsTab_WithoutAsking()
    {
        var change = Change(UndoChangeKind.SettingsWatchdog, opensTab: UndoChangesService.WatchdogTab);
        var (vm, service, navigation) = NewVm(Scan(change));
        using var dialog = new DialogAnswer(confirm: true);

        await vm.PutBackCommand.ExecuteAsync(vm.Changes[0]);

        navigation.Received(1).GoTo("nav-settings-watchdog");
        Assert.Equal(0, dialog.Calls);
        await service.DidNotReceive().PutBackAsync(Arg.Any<UndoChange>(), Arg.Any<CancellationToken>());
        Assert.Equal("Opens Settings Watchdog, where each setting is put back on its own.", vm.Changes[0].ButtonHint);
    }

    [Theory]
    [InlineData(UndoChangeKind.PerformanceMode, "Putting Performance Mode's original settings back…")]
    [InlineData(UndoChangeKind.Services, "Turning the services back on…")]
    [InlineData(UndoChangeKind.HostsFile, "Restoring the hosts file from the copy beside it…")]
    [InlineData(UndoChangeKind.EnvironmentVariables, "Restoring the environment variables from SysManager's copy…")]
    [InlineData(UndoChangeKind.GamingProfile, "Turning game mode off…")]
    public void WhileAChangeGoesBack_TheStatusLineSaysItInTheButtonsWords(UndoChangeKind kind, string expected) =>
        Assert.Equal(expected, UndoChangesViewModel.ProgressText(kind));

    // ── Looking again ───────────────────────────────────────────────────────

    [Fact]
    public async Task BeingShown_LooksAgain()
    {
        var (vm, service, _) = NewVm(Scan(), Scan(Change()));

        vm.IsActive = true;
        await vm.ShownRefresh;

        await service.Received(2).ScanAsync(Arg.Any<CancellationToken>());
        Assert.Single(vm.Changes);
    }

    [Fact]
    public async Task BeingShown_WhileTheFirstLookRuns_DoesNotLookTwice()
    {
        // The tab is shown the moment it is built, and that first look is already the fresh one.
        var service = Service();
        var first = new TaskCompletionSource<UndoScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ScanAsync(Arg.Any<CancellationToken>()).Returns(first.Task);
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());

        vm.IsActive = true;
        first.SetResult(Scan());
        await vm.InitializationComplete;
        await vm.ShownRefresh;

        await service.Received(1).ScanAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALookAskedForWhileOneRuns_HappensOnceItEnds()
    {
        var change = Change();
        var service = Service();
        var first = new TaskCompletionSource<UndoScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ScanAsync(Arg.Any<CancellationToken>()).Returns(first.Task, Task.FromResult(Scan(change)));
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());

        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        first.SetResult(Scan());
        await vm.InitializationComplete;
        await refresh;

        await service.Received(2).ScanAsync(Arg.Any<CancellationToken>());
        Assert.Single(vm.Changes);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Refresh_IsOff_WhileALookRuns()
    {
        // The shell asks before it runs F5, and a held key would otherwise queue a look, and a question to Windows about
        // its restore points, for every repeat.
        var service = Service();
        var first = new TaskCompletionSource<UndoScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ScanAsync(Arg.Any<CancellationToken>()).Returns(first.Task);
        var vm = new UndoChangesViewModel(service, Substitute.For<INavigationService>());

        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        // The Refresh button reads it again only when told.
        var told = 0;
        vm.RefreshCommand.CanExecuteChanged += (_, _) => told++;

        first.SetResult(Scan());
        await vm.InitializationComplete;

        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(told > 0);
    }

    [Fact]
    public async Task TheRestorePoint_IsNotAskedForAgain_WhileTheLastQuestionIsOut()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, service, _) = NewVm();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<RestorePointLook>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LookForRestorePointAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            asked.TrySetResult();
            return answer.Task;
        });

        vm.IsActive = true;
        var first = vm.RestorePointLookup;
        // Windows is asked from a worker thread, so the question is waited for before anything is counted.
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(first, vm.RestorePointLookup);
        await service.Received(1).LookForRestorePointAsync(Arg.Any<CancellationToken>());

        answer.SetResult(new RestorePointLook(Newest, Listed: true));
        await first;
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RestorePointLookup;

        Assert.NotSame(first, vm.RestorePointLookup);
        await service.Received(2).LookForRestorePointAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosingTheTab_CallsOffTheRestorePointQuestion()
    {
        // One Windows never answers must not outlive the tab.
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, service, _) = NewVm();
        var answer = new TaskCompletionSource<RestorePointLook>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken asked = default;
        service.LookForRestorePointAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            asked = call.Arg<CancellationToken>();
            asked.Register(() => answer.TrySetCanceled(asked));
            return answer.Task;
        });
        vm.IsActive = true;

        vm.Dispose();
        // Bounded, so a question that was not called off fails here rather than hanging the run.
        await vm.RestorePointLookup.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(asked.IsCancellationRequested);
        Assert.Equal("Looking for the newest restore point…", vm.RestorePointText);
    }

    [Fact]
    public async Task F5_LooksAgain()
    {
        var (vm, service, _) = NewVm(Scan(), Scan(Change()));

        Assert.Same(vm.RefreshCommand, vm.RefreshOnF5);
        await ((IAsyncRelayCommand)vm.RefreshOnF5!).ExecuteAsync(null);

        await service.Received(2).ScanAsync(Arg.Any<CancellationToken>());
        Assert.Single(vm.Changes);
    }

    // ── The links ───────────────────────────────────────────────────────────

    [Fact]
    public void EverySwitchLink_OpensItsOwnTab()
    {
        var (vm, _, navigation) = NewVm();

        foreach (var link in vm.Switches)
            vm.OpenTabCommand.Execute(link.NavId);

        Assert.Equal(6, vm.Switches.Count);
        Assert.Equal(vm.Switches.Count, vm.Switches.Select(s => s.NavId).Distinct().Count());
        foreach (var link in vm.Switches)
            navigation.Received(1).GoTo(link.NavId);
    }

    [Fact]
    public void ASwitchLink_ReadsAsItsWords_ForAScreenReader()
    {
        var link = new UndoSwitch("DNS & Hosts", "DNS back to automatic", "nav-dns-hosts");

        Assert.Equal("DNS & Hosts: DNS back to automatic", link.ToString());
        Assert.Equal("Open DNS & Hosts", link.ButtonName);
    }

    [Fact]
    public void ARow_IsAnnouncedByItsWords_AndItsButtonByItsTitle()
    {
        var row = new UndoChangeRow(Change(UndoChangeKind.HostsFile, needsAdmin: true), canAct: false);

        Assert.Equal("HostsFile. HostsFile detail First changed 5 Oct 2026, 19:40. Needs administrator rights.", row.ToString());
        Assert.Equal("Put back — HostsFile", row.ButtonName);
    }

    [Theory]
    [InlineData(UndoChangeKind.PerformanceMode, "\uE770")]
    [InlineData(UndoChangeKind.Services, "\uE770")]
    [InlineData(UndoChangeKind.HostsFile, "\uE968")]
    [InlineData(UndoChangeKind.EnvironmentVariables, "\uE713")]
    [InlineData(UndoChangeKind.GamingProfile, "\uE7FC")]
    [InlineData(UndoChangeKind.SettingsWatchdog, "\uE9D9")]
    public void ARowsIcon_IsItsSidebarGroups(UndoChangeKind kind, string glyph) =>
        Assert.Equal(glyph, new UndoChangeRow(Change(kind), canAct: true).Glyph);

    [Theory]
    [InlineData(UndoChangeKind.PerformanceMode, "Put Performance Mode's original settings back")]
    [InlineData(UndoChangeKind.Services, "Turned services back on")]
    [InlineData(UndoChangeKind.HostsFile, "Restored the hosts file from the copy beside it")]
    [InlineData(UndoChangeKind.EnvironmentVariables, "Restored the environment variables from SysManager's copy")]
    [InlineData(UndoChangeKind.GamingProfile, "Turned game mode off")]
    public void ThePutBack_IsLoggedInWords(UndoChangeKind kind, string expected) =>
        Assert.Equal(expected, UndoChangesViewModel.ActivityText(kind));
}
