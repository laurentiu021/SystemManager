// SysManager · GamingProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="GamingProfileViewModel"/>. The whole feature sits behind
/// <see cref="IGamingProfileService"/> and <see cref="ICpuAffinityService"/>, so both are
/// substituted — no real power/timer/registry/service mutation happens. Coverage targets the
/// VM logic that matters: the last-config seeds the toggles, CanApply gating, that Start
/// forwards the built profile + selected game to the service, that Stop reverts, that the
/// auto-revert event flips the UI state, and the pure honest-reporting summary. The recovery
/// prompt path is kept off (HasPendingRecovery=false) so construction never raises a dialog.
///
/// <para>Serialized on the DialogService collection: the Start/Stop confirm-gate tests swap
/// the process-wide <see cref="DialogService.Instance"/>, matching the established pattern.</para>
/// </summary>
[Collection("ProcessWideStatics")]
public class GamingProfileViewModelTests
{
    // Swap DialogService.Instance with a substitute that returns <paramref name="confirm"/>,
    // run the action, always restore the previous instance (mirrors AppBlockerViewModelTests).
    private static async Task WithConfirm(bool confirm, Func<Task> action)
    {
        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(confirm);
        DialogService.Instance = dialog;
        try { await action(); }
        finally { DialogService.Instance = prev; }
    }

    private static IGamingProfileService ServiceWith(GamingProfile? lastConfig = null, bool active = false)
    {
        var svc = Substitute.For<IGamingProfileService>();
        svc.LoadLastConfig().Returns(lastConfig ?? new GamingProfile());
        svc.IsActive.Returns(active);
        svc.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.None));
        // Nothing has asked yet in this run, as when the tab is opened before the Dashboard got there.
        svc.ClaimRecoveryQuestion().Returns(true);
        svc.RevertAsync(Arg.Any<CancellationToken>()).Returns(GamingRevertResult.Complete);
        svc.RecoverPendingAsync(Arg.Any<CancellationToken>()).Returns(GamingRevertResult.Complete);
        return svc;
    }

    private static ICpuAffinityService CpuWith(params RunningProcess[] procs)
    {
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns(procs.ToList());
        return cpu;
    }

    private static GamingProfileViewModel NewVm(IGamingProfileService service, ICpuAffinityService? cpu = null)
    {
        var vm = new GamingProfileViewModel(service, cpu ?? CpuWith());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // ── Construction seeds toggles from the last-used config ───────────────

    [Fact]
    public void Constructor_SeedsToggles_FromLastConfig()
    {
        var vm = NewVm(ServiceWith(new GamingProfile
        {
            UltimatePerformancePlan = true,
            SilenceNotifications = true,
            DisableVisualEffects = false,
        }));

        Assert.True(vm.UltimatePerformancePlan);
        Assert.True(vm.SilenceNotifications);
        Assert.False(vm.DisableVisualEffects);
    }

    [Fact]
    public void Constructor_PopulatesProcessList()
    {
        var vm = NewVm(ServiceWith(), CpuWith(new RunningProcess(10, "game.exe", 0), new RunningProcess(20, "other.exe", 0)));
        Assert.Equal(2, vm.Processes.Count);
    }

    // ── CanApply gating ─────────────────────────────────────────────────────

    [Fact]
    public void CanApply_False_WhenNoToggleEnabled()
    {
        var vm = NewVm(ServiceWith(new GamingProfile())); // all off
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void CanApply_True_WhenAToggleEnabled_AndNotActive()
    {
        var vm = NewVm(ServiceWith(new GamingProfile()));
        vm.SilenceNotifications = true;
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void CanApply_False_WhenSessionAlreadyActive()
    {
        var vm = NewVm(ServiceWith(GamingProfile.Default, active: true));
        vm.IsSessionActive = true;
        Assert.False(vm.CanApply); // can't start a second session over an active one
    }

    // ── Start forwards the built profile + selected game ───────────────────

    private static readonly DateTime Started = new(2026, 9, 30, 8, 15, 42, DateTimeKind.Local);

    [Fact]
    public async Task Start_ForwardsProfileAndGame_ToService()
    {
        var service = ServiceWith(new GamingProfile());
        service.ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>())
               .Returns(new GamingApplyResult([], false));
        var cpu = CpuWith(new RunningProcess(4242, "doom.exe", 0, Started));
        var vm = NewVm(service, cpu);

        vm.FinestTimerResolution = true;
        vm.HighGameCpuPriority = true;
        vm.SelectedGame = vm.Processes.Single();

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        service.Received(1).SaveLastConfig(
            Arg.Is<GamingProfile>(p => p != null && p.FinestTimerResolution && p.HighGameCpuPriority));
        // The start time goes with the ID, so the service can tell the game from a new program with its ID (#2559).
        await service.Received(1).ApplyAsync(
            Arg.Is<GamingProfile>(p => p != null && p.FinestTimerResolution && p.HighGameCpuPriority),
            Arg.Is<GameTarget?>(g => g != null && g.ProcessId == 4242 && g.Name == "doom.exe" && g.StartTime == Started));
    }

    [Fact]
    public async Task Start_WhenTheGameHadClosed_SaysSo_AndTakesItOffTheList()
    {
        // #2559. The list can be minutes old; the service refused before changing anything.
        var service = ServiceWith(new GamingProfile());
        service.ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>())
               .Returns(new GamingApplyResult([], false, GameClosed: true));
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns(
            new List<RunningProcess> { new(4242, "doom.exe", 0, Started) },   // at load
            new List<RunningProcess>());                                       // the refresh after the refusal
        var vm = NewVm(service, cpu);
        vm.HighGameCpuPriority = true;
        vm.SelectedGame = vm.Processes.Single();

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        Assert.Equal("doom.exe had already closed, so game mode did not start. Pick the game again from the refreshed list.",
            vm.StatusMessage);
        Assert.Null(vm.SelectedGame);
        Assert.Empty(vm.Processes);
        Assert.False(vm.IsSessionActive);
    }

    /// <summary>
    /// A service that made the changes, found the game gone, and ended the session before returning (#2563), and a
    /// process list that has the game at load and not at the refresh after.
    /// </summary>
    private static (IGamingProfileService Service, ICpuAffinityService Cpu) EndsAtStart(GamingRevertResult undone)
    {
        var service = ServiceWith(new GamingProfile());
        service.ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>())
               .Returns(new GamingApplyResult([new GamingStepOutcome("High game CPU priority", GamingStepStatus.Applied)],
                   RestorePointCreated: false, EndedAtStart: undone));
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns(
            new List<RunningProcess> { new(4242, "doom.exe", 0, Started) },
            new List<RunningProcess>());
        return (service, cpu);
    }

    [Fact]
    public async Task Start_WhenTheGameClosesWhileGameModeStarts_SaysItEnded_AndTakesTheGameOffTheList()
    {
        var (service, cpu) = EndsAtStart(GamingRevertResult.Complete);
        var vm = NewVm(service, cpu);
        vm.HighGameCpuPriority = true;
        vm.SelectedGame = vm.Processes.Single();

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        Assert.Equal(
            "doom.exe closed while game mode was starting, so game mode ended and original settings were restored.",
            vm.StatusMessage);
        Assert.False(vm.IsSessionActive);
        Assert.Null(vm.SelectedGame);
        Assert.Empty(vm.Processes);
    }

    [Fact]
    public async Task Refresh_DoesNotCarryTheSelectionToANewProcessWithTheSameId()
    {
        // A restarted game can come back with the same name and, by chance, the same ID. It is not the process that
        // was picked, so nothing is selected rather than it (#2559).
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns(
            new List<RunningProcess> { new(4242, "doom.exe", 0, Started) },
            new List<RunningProcess> { new(4242, "doom.exe", 0, Started.AddMinutes(7)) });
        var vm = NewVm(ServiceWith(), cpu);
        vm.SelectedGame = vm.Processes.Single();

        await vm.RefreshProcessesCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedGame);
    }

    [Fact]
    public async Task Start_WhenTheServiceCouldNotReadItsStore_SaysSo_AndStartsNothing()
    {
        var service = ServiceWith(new GamingProfile());
        service.ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>())
               .Returns(new GamingApplyResult([], false, StoreUnreadable: true));
        var vm = NewVm(service);
        vm.SilenceNotifications = true;

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        Assert.Contains("could not read the record it keeps to undo game mode", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.IsSessionActive);
    }

    [Fact]
    public async Task Start_Cancelled_DoesNotCallService()
    {
        var service = ServiceWith(new GamingProfile());
        var vm = NewVm(service);
        vm.SilenceNotifications = true;

        await WithConfirm(false, () => vm.StartCommand.ExecuteAsync(null)); // user cancels

        await service.DidNotReceive().ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>());
    }

    [Fact]
    public async Task Start_SaysWhatTheRestorePointDoesToSystemProtection()
    {
        // #2483. Starting takes the session restore point first, which turns System Protection back on when it
        // is off, and this confirmation never said so. Stop and the crash recovery take none.
        var service = ServiceWith(new GamingProfile());
        service.RestorePointNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        var vm = NewVm(service);
        vm.SilenceNotifications = true;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }

    // ── Stop reverts through the service ───────────────────────────────────

    [Fact]
    public async Task Stop_RevertsThroughService()
    {
        var service = ServiceWith(GamingProfile.Default, active: true);
        var vm = NewVm(service);
        vm.IsSessionActive = true;

        await WithConfirm(true, () => vm.StopCommand.ExecuteAsync(null));

        await service.Received(1).RevertAsync(Arg.Any<CancellationToken>());
    }

    // ── Auto-revert event flips the UI state ───────────────────────────────

    [Fact]
    public void SessionAutoReverted_Event_ClearsActiveState()
    {
        var service = ServiceWith(GamingProfile.Default, active: true);
        var vm = NewVm(service);
        vm.IsSessionActive = true;

        // The bound game exited: the service reverted and now reports inactive, then raises.
        service.IsActive.Returns(false);
        service.SessionAutoReverted += Raise.Event<EventHandler<GamingRevertResult>>(service, GamingRevertResult.Complete);

        Assert.False(vm.IsSessionActive);
    }

    // ── A revert that could not restore everything says so (#2445) ─────────
    //
    // Each of the three paths used to write a fixed "restored" sentence whatever the revert achieved.

    private static readonly GamingRevertResult PowerPlanNotRestored = new(["Ultimate Performance power plan"]);

    [Fact]
    public async Task Stop_WhenASettingCouldNotBeRestored_NamesIt_NotThatEverythingWasRestored()
    {
        var service = ServiceWith(GamingProfile.Default, active: true);
        service.RevertAsync(Arg.Any<CancellationToken>()).Returns(PowerPlanNotRestored);
        var vm = NewVm(service);
        vm.IsSessionActive = true;

        await WithConfirm(true, () => vm.StopCommand.ExecuteAsync(null));

        Assert.DoesNotContain("original settings restored", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("\"Ultimate Performance power plan\" was not restored", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void GameExit_WhenASettingCouldNotBeRestored_NamesIt()
    {
        var service = ServiceWith(GamingProfile.Default, active: true);
        var vm = NewVm(service);
        vm.IsSessionActive = true;
        service.IsActive.Returns(false);

        service.SessionAutoReverted += Raise.Event<EventHandler<GamingRevertResult>>(service, PowerPlanNotRestored);

        Assert.DoesNotContain("original settings were restored", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("\"Ultimate Performance power plan\" was not restored", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameClosedWhileStarting_WhenASettingCouldNotBeRestored_NamesIt()
    {
        // The fourth way a session ends (#2563) reports a partial restore as the other three do.
        var (service, cpu) = EndsAtStart(PowerPlanNotRestored);
        var vm = NewVm(service, cpu);
        vm.HighGameCpuPriority = true;
        vm.SelectedGame = vm.Processes.Single();

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        Assert.StartsWith("doom.exe closed while game mode was starting, so game mode ended, but", vm.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("original settings were restored", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("\"Ultimate Performance power plan\" was not restored", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_WhenASettingCouldNotBeRestored_NamesIt()
    {
        var service = ServiceWith(GamingProfile.Default);
        service.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.LeftOn));
        service.RecoverPendingAsync(Arg.Any<CancellationToken>()).Returns(PowerPlanNotRestored);
        GamingProfileViewModel? vm = null;

        // The recovery prompt runs during initialisation, so the dialog answer must cover construction.
        await WithConfirm(true, async () =>
        {
            vm = new GamingProfileViewModel(service, CpuWith());
            await vm.InitializationComplete;
        });

        await service.Received(1).RecoverPendingAsync(Arg.Any<CancellationToken>());
        Assert.Contains("\"Ultimate Performance power plan\" was not restored", vm!.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_IsNotAskedAgain_WhenTheDashboardAlreadyAskedInThisRun()
    {
        // The Dashboard asks at startup (#2592). Opening the tab afterwards must not put the same question a second time,
        // whatever the answer was.
        var service = ServiceWith();
        service.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.LeftOn));
        service.ClaimRecoveryQuestion().Returns(false);
        using var dialog = new DialogAnswer(confirm: true);

        var vm = new GamingProfileViewModel(service, CpuWith());
        await vm.InitializationComplete;

        Assert.Equal(0, dialog.Calls);
        await service.DidNotReceive().RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recovery_ThatCannotReadItsRecord_SaysNothingWasReverted()
    {
        // The record could not be read once the question was answered, so nothing was reverted, and the record is kept.
        var service = ServiceWith();
        service.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.LeftOn));
        service.RecoverPendingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GamingRevertResult>(new System.IO.IOException("The file is held by another program.")));
        GamingProfileViewModel? vm = null;

        await WithConfirm(true, async () =>
        {
            vm = new GamingProfileViewModel(service, CpuWith());
            await vm.InitializationComplete;
        });

        Assert.Equal("SysManager could not read its record of the previous session just now, so nothing was reverted. It "
            + "asks again the next time it starts.", vm!.StatusMessage);
    }

    [Fact]
    public async Task Recovery_IsNotOffered_WhenWhetherASessionWasLeftOnCannotBeRead()
    {
        // "Not known" is not "left on": offering to revert a session that may not exist would change settings for
        // nothing. The next launch asks again.
        var service = ServiceWith();
        service.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.Unreadable));
        var dialog = Substitute.For<IDialogService>();
        var previous = DialogService.Instance;
        DialogService.Instance = dialog;
        try
        {
            var vm = new GamingProfileViewModel(service, CpuWith());
            await vm.InitializationComplete;
        }
        finally
        {
            DialogService.Instance = previous;
        }

        dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        await service.DidNotReceive().RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DescribeRevert_NamesEverySettingThatWasNotRestored()
    {
        var text = GamingProfileViewModel.DescribeRevert(
            new GamingRevertResult(["Ultimate Performance power plan", "Pause search indexing"]),
            "every setting restored", "Game mode stopped");

        Assert.StartsWith("Game mode stopped, but 2 settings were (", text, StringComparison.Ordinal);
        Assert.Contains("Ultimate Performance power plan, Pause search indexing", text, StringComparison.Ordinal);
        Assert.Contains("check them yourself", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRevert_WhenEverySettingWasRestored_UsesTheSuccessLine()
        => Assert.Equal("every setting restored",
            GamingProfileViewModel.DescribeRevert(GamingRevertResult.Complete, "every setting restored", "unused"));

    // ── DescribeResult: honest, plain-language summary (pure) ──────────────

    [Fact]
    public void DescribeResult_AllApplied_NoAdmin_ReadsCleanly()
    {
        var result = new GamingApplyResult(
            [new GamingStepOutcome("a", GamingStepStatus.Applied),
             new GamingStepOutcome("b", GamingStepStatus.Applied)],
            RestorePointCreated: false);

        var text = GamingProfileViewModel.DescribeResult(result, new GameTarget(1, "doom.exe", StartTime: null));

        Assert.Contains("2 optimization(s) applied", text);
        Assert.Contains("doom.exe", text);
        Assert.DoesNotContain("administrator", text);
    }

    [Fact]
    public void DescribeResult_SkippedForAdmin_IsSurfacedHonestly()
    {
        var result = new GamingApplyResult(
            [new GamingStepOutcome("a", GamingStepStatus.Applied),
             new GamingStepOutcome("b", GamingStepStatus.SkippedNeedsAdmin),
             new GamingStepOutcome("c", GamingStepStatus.Failed)],
            RestorePointCreated: true);

        var text = GamingProfileViewModel.DescribeResult(result, game: null);

        Assert.Contains("1 optimization(s) applied", text);
        Assert.Contains("1 need administrator", text);
        Assert.Contains("1 could not be applied", text);
        Assert.Contains("restore point created", text);
    }
}
