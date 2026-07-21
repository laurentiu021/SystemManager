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
[Collection("DialogService")]
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
        svc.HasPendingRecovery.Returns(false);
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

    [Fact]
    public async Task Start_ForwardsProfileAndGame_ToService()
    {
        var service = ServiceWith(new GamingProfile());
        service.ApplyAsync(Arg.Any<GamingProfile>(), Arg.Any<GameTarget?>())
               .Returns(new GamingApplyResult([], false));
        var cpu = CpuWith(new RunningProcess(4242, "doom.exe", 0));
        var vm = NewVm(service, cpu);

        vm.FinestTimerResolution = true;
        vm.HighGameCpuPriority = true;
        vm.SelectedGame = vm.Processes.Single();

        await WithConfirm(true, () => vm.StartCommand.ExecuteAsync(null));

        service.SaveLastConfig(Arg.Is<GamingProfile>(p => p != null && p.FinestTimerResolution && p.HighGameCpuPriority));
        await service.Received(1).ApplyAsync(
            Arg.Is<GamingProfile>(p => p != null && p.FinestTimerResolution && p.HighGameCpuPriority),
            Arg.Is<GameTarget?>(g => g != null && g.ProcessId == 4242 && g.Name == "doom.exe"));
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
        service.SessionAutoReverted += Raise.Event<EventHandler>(service, EventArgs.Empty);

        Assert.False(vm.IsSessionActive);
    }

    // ── DescribeResult: honest, plain-language summary (pure) ──────────────

    [Fact]
    public void DescribeResult_AllApplied_NoAdmin_ReadsCleanly()
    {
        var result = new GamingApplyResult(
            [new GamingStepOutcome("a", GamingStepStatus.Applied),
             new GamingStepOutcome("b", GamingStepStatus.Applied)],
            RestorePointCreated: false);

        var text = GamingProfileViewModel.DescribeResult(result, new GameTarget(1, "doom.exe"));

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
