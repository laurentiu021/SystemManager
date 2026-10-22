// SysManager · NetworkRepairViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the flush-DNS gate test swaps the static DialogService.Instance.
[Collection("ProcessWideStatics")]
public class NetworkRepairViewModelTests
{
    private static NetworkSharedState NewShared(IPowerShellRunner? runner = null) =>
        new(new PingMonitorService(), new TracerouteService(), new TracerouteMonitorService(),
            new SpeedTestService(), new NetworkRepairService(runner ?? new PowerShellRunner()));

    // A tab whose restore point nothing asks Windows for.
    private static NetworkRepairViewModel NewVm(NetworkSharedState shared) =>
        new(shared, Substitute.For<ISessionRestorePoint>());

    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = NewShared();
        var vm = NewVm(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotRepairing()
    {
        var shared = NewShared();
        var vm = NewVm(shared);
        Assert.False(vm.IsRepairing);
        Assert.Equal("", vm.RepairStatus);
        Assert.False(vm.RepairNeedsReboot);
    }

    [Fact]
    public void Repairing_DrivesIsBusy_SoTheSidebarShowsProgress()
    {
        // NavItem forwards ViewModelBase.IsBusy to the slim progress bar under the tab's name, which is the
        // only sign — while the user is looking at another tab — that this one is working. Five tabs kept a
        // running flag and never assigned IsBusy, so their bar never appeared; this asserts the generated
        // On…Changed hook actually fires, which the source-shape guard in ArchitectureTests cannot.
        //
        // One behaviour test for the mechanism rather than five identical ones: the other four are the same
        // one-line shape, and EveryViewModelThatTracksRunningState_ForwardsItToIsBusy is what keeps them there.
        var vm = NewVm(NewShared());
        Assert.False(vm.IsBusy);

        vm.IsRepairing = true;
        Assert.True(vm.IsBusy, "a repair is running and the shell was never told");

        vm.IsRepairing = false;
        Assert.False(vm.IsBusy, "the bar has to clear when the work finishes, or the tab looks stuck");
    }

    [Fact]
    public async Task FlushDns_WhenUserDeclinesConfirm_DoesNothing()
    {
        var vm = NewVm(NewShared());

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            await vm.FlushDnsCommand.ExecuteAsync(null);

            // Declining must prompt once and then short-circuit before any repair runs:
            // status stays empty and the VM never enters the repairing state.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Equal("", vm.RepairStatus);
            Assert.False(vm.IsRepairing);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── Elevation gate on Winsock / TCP-IP reset ─────────────────────────────
    //
    // Both resets refuse without administrator rights and neither refusal was asserted (#2171). Flush DNS
    // deliberately has no gate — it needs no elevation — which is why only these two are covered here.
    //
    // The elevated counterpart DECLINES the confirmation. Past the dialog these commands run netsh
    // against the machine's real network stack and require a reboot, so a test that confirmed would
    // reset Winsock on whatever ran the suite. Declining proves the gate let it through and stops there.

    [Theory]
    [InlineData("Winsock")]
    [InlineData("TcpIp")]
    public async Task Reset_WhenNotElevated_SaysSoAndNeverPromptsConfirm(string which)
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm(NewShared());

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // would say yes if asked
        DialogService.Instance = dialog;
        try
        {
            await ExecuteResetAsync(vm, which);

            Assert.Contains("administrator", vm.RepairStatus, StringComparison.OrdinalIgnoreCase);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(vm.IsRepairing);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Theory]
    [InlineData("Winsock")]
    [InlineData("TcpIp")]
    public async Task Reset_WhenElevated_AsksBeforeTouchingTheNetworkStack(string which)
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var vm = NewVm(NewShared());

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // decline, so nothing runs
        DialogService.Instance = dialog;
        try
        {
            await ExecuteResetAsync(vm, which);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Equal("", vm.RepairStatus);
            Assert.False(vm.IsRepairing);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── The session restore point before a reset (#2843) ─────────────────────
    //
    // Every other tab that changes Windows takes the session's restore point before its first change; the two resets
    // took none, and TCP/IP's has no way back in SysManager. These run the resets on a substituted runner, so netsh
    // never runs and nothing on this PC's network changes.

    // A tab over a runner whose every process exits 0, and the session restore point it was given.
    private static (NetworkRepairViewModel Vm, IPowerShellRunner Runner, ISessionRestorePoint RestorePoint) NewVmOver(
        bool pointMade = false)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
            .Returns(0);
        var restorePoint = Substitute.For<ISessionRestorePoint>();
        restorePoint.ConfirmationNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        restorePoint.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(pointMade);
        return (new NetworkRepairViewModel(NewShared(runner), restorePoint), runner, restorePoint);
    }

    [Theory]
    [InlineData("Winsock")]
    [InlineData("TcpIp")]
    public async Task Reset_AsksWithTheSessionRestorePointsNotice(string which)
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, runner, _) = NewVmOver();
        using var dialog = new DialogAnswer(confirm: false);

        await ExecuteResetAsync(vm, which);

        var asked = Assert.Single(dialog.Messages);
        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, asked, StringComparison.Ordinal);
        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("Winsock", "winsock reset", false)]
    [InlineData("Winsock", "winsock reset", true)]
    [InlineData("TcpIp", "int ip reset", false)]
    [InlineData("TcpIp", "int ip reset", true)]
    public async Task Reset_TakesTheRestorePointBeforeNetshRuns_AndClaimsOneOnlyWhenItWasMade(
        string which, string netsh, bool pointMade)
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, runner, restorePoint) = NewVmOver(pointMade);
        using var dialog = new DialogAnswer(confirm: true);

        await ExecuteResetAsync(vm, which);

        Received.InOrder(() =>
        {
            restorePoint.EnsureAsync("SysManager Network Repair", Arg.Any<CancellationToken>());
            runner.RunProcessAsync("netsh.exe", netsh, Arg.Any<CancellationToken>(), Arg.Any<Encoding?>());
        });
        Assert.Equal(pointMade, vm.RepairStatus.Contains("Restore point created.", StringComparison.Ordinal));
        Assert.StartsWith("✓", vm.RepairStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushDns_TakesNoRestorePoint_AndAsksWithoutItsNotice()
    {
        // It changes nothing that a restore point keeps, and needs no administrator rights.
        var (vm, runner, restorePoint) = NewVmOver(pointMade: true);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.FlushDnsCommand.ExecuteAsync(null);

        var asked = Assert.Single(dialog.Messages);
        Assert.DoesNotContain(SessionRestorePointTests.NoticeStandIn, asked, StringComparison.Ordinal);
        await restorePoint.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        await runner.Received(1).RunProcessAsync("ipconfig.exe", "/flushdns", Arg.Any<CancellationToken>(),
            Arg.Any<Encoding?>());
    }

    [Fact]
    public async Task TcpIpReset_SaysWhatItRemovesBeforeItRuns()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, _, _) = NewVmOver();
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ResetTcpIpCommand.ExecuteAsync(null);

        var asked = Assert.Single(dialog.Messages);
        Assert.Contains("fixed IP address", asked, StringComparison.Ordinal);
        Assert.Contains("routes", asked, StringComparison.Ordinal);
        Assert.Contains("removed", asked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Winsock")]
    [InlineData("TcpIp")]
    public async Task Reset_WhileAnotherSystemChangeRuns_TakesNoRestorePointAndRunsNothing(string which)
    {
        // The point snapshots the system drive and the reset changes Windows' settings: neither may overlap an SFC
        // repair, a feature change or an update install.
        using var elevated = AdminHelper.ForceElevation(true);
        var (vm, runner, restorePoint) = NewVmOver(pointMade: true);
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);

        await ExecuteResetAsync(vm, which);

        Assert.Equal("Cannot start — SFC scan is already running.", vm.RepairStatus);
        await restorePoint.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
        Assert.False(vm.IsRepairing);
    }

    private static Task ExecuteResetAsync(NetworkRepairViewModel vm, string which) => which switch
    {
        "Winsock" => vm.ResetWinsockCommand.ExecuteAsync(null),
        "TcpIp" => vm.ResetTcpIpCommand.ExecuteAsync(null),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "unknown reset"),
    };
}
