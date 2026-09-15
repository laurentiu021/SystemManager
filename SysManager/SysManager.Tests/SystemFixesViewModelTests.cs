// SysManager · SystemFixesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Reflection;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="SystemFixesViewModel"/> — the tab that owns every Windows repair.
/// Nothing here spawns a process: the runner is a substitute, so the assertions are about which
/// command line the view-model WOULD launch and which gates stop it first.
/// </summary>
/// <remarks>
/// SFC and DISM /RestoreHealth arrived here from Quick Cleanup (#1493), and their tests came with
/// them. Two of these are new rather than moved, and both close a gap the move opened: the old
/// elevation test asserted only that the status message mentioned admin, which a command that
/// refuses unconditionally would also satisfy, and there was no positive test for DISM at all.
/// <para>Serialized: <see cref="AdminHelper.ForceElevation"/>, <see cref="DialogService.Instance"/>
/// and <see cref="OperationLockService"/> are all process-wide statics.</para>
/// </remarks>
[Collection("ProcessWideStatics")]
public class SystemFixesViewModelTests
{
    private static SystemFixesViewModel NewVm(
        IPowerShellRunner? runner = null, IPowerShellRunner? serviceRunner = null) =>
        new(new SystemFixService(serviceRunner ?? Substitute.For<IPowerShellRunner>()),
            runner ?? Substitute.For<IPowerShellRunner>());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SetsConsoleInstance()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_DefaultsAllRunningFlagsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsFixRunning);
        Assert.False(vm.IsSfcRunning);
        Assert.False(vm.IsDismRunning);
        Assert.False(vm.IsAnyRunning);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_DefaultsStatusStringsToIdle()
    {
        var vm = NewVm();
        Assert.Equal("Idle", vm.SfcStatus);
        Assert.Equal("Idle", vm.DismStatus);
    }

    [Fact]
    public void Constructor_SaysWhatToDo()
    {
        var vm = NewVm();
        Assert.Contains("Pick a repair", vm.StatusMessage);
    }

    // ---------- IsAnyRunning / IsBusy aggregation ----------

    [Theory]
    [InlineData(nameof(SystemFixesViewModel.IsFixRunning))]
    [InlineData(nameof(SystemFixesViewModel.IsSfcRunning))]
    [InlineData(nameof(SystemFixesViewModel.IsDismRunning))]
    public void IsAnyRunning_TurnsTrueWhenAnyFlagFlipsOn(string propName)
    {
        var vm = NewVm();
        var p = typeof(SystemFixesViewModel).GetProperty(propName)!;
        p.SetValue(vm, true);
        Assert.True(vm.IsAnyRunning);
        Assert.True(vm.IsBusy);
    }

    [Fact]
    public void IsAnyRunning_FiresPropertyChanged()
    {
        var vm = NewVm();
        var seen = vm.RecordPropertyChanges();

        vm.IsSfcRunning = true;

        Assert.Contains("IsAnyRunning", seen);
    }

    [Fact]
    public void IsBusy_IsClearOnceEveryRepairHasFinished()
    {
        var vm = NewVm();
        vm.IsFixRunning = true;
        vm.IsSfcRunning = true;
        vm.IsDismRunning = true;

        vm.IsFixRunning = false;
        vm.IsSfcRunning = false;
        vm.IsDismRunning = false;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsAnyRunning);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("RunSfcCommand")]
    [InlineData("RunDismCommand")]
    [InlineData("ResetWindowsUpdateCommand")]
    [InlineData("ReinstallWinGetCommand")]
    [InlineData("OpenAutologinCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- the elevation gate ----------

    /// <summary>
    /// Every repair on the tab is disabled without administrator rights, and re-enabled with them.
    /// </summary>
    /// <remarks>
    /// Both directions in one test on purpose: a CanExecute that returns false unconditionally would
    /// satisfy the negative half alone. The view-model is built INSIDE each scope because it caches
    /// elevation in its constructor.
    /// </remarks>
    [Theory]
    [InlineData("RunSfcCommand")]
    [InlineData("RunDismCommand")]
    [InlineData("ResetWindowsUpdateCommand")]
    [InlineData("ReinstallWinGetCommand")]
    public void EveryRepair_IsClickableOnlyWhenElevated(string name)
    {
        using (AdminHelper.ForceElevation(false))
        {
            var vm = NewVm();
            var command = (System.Windows.Input.ICommand)vm.GetType().GetProperty(name)!.GetValue(vm)!;
            Assert.False(command.CanExecute(null));
        }

        using (AdminHelper.ForceElevation(true))
        {
            var vm = NewVm();
            var command = (System.Windows.Input.ICommand)vm.GetType().GetProperty(name)!.GetValue(vm)!;
            Assert.True(command.CanExecute(null));
        }
    }

    /// <summary>
    /// And the gate is in the command BODY, not only in CanExecute.
    /// </summary>
    /// <remarks>
    /// <c>ExecuteAsync</c> runs the body regardless of what <c>CanExecute</c> said, so a disabled
    /// button is an affordance rather than a guard. Asserting on the runner is what makes this test
    /// worth having: the previous version checked only that the status message contained "admin",
    /// which would also pass if the repair had launched sfc.exe and then complained.
    /// </remarks>
    [Fact]
    public async Task RunSfc_WhenNotElevated_NeverReachesTheRunner()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.False(vm.IsElevated, "the scope must reach the view-model's constructor");

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Contains("administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsSfcRunning);
        Assert.False(vm.IsAnyRunning);
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RunDism_WhenNotElevated_NeverReachesTheRunner()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.False(vm.IsElevated);

        await vm.RunDismCommand.ExecuteAsync(null);

        Assert.Contains("administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsDismRunning);
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// The scripted repairs are gated in their body too — and before the confirmation dialog, so an
    /// unelevated caller is never asked to confirm something that cannot run.
    /// </summary>
    [Fact]
    public async Task ResetWindowsUpdate_WhenNotElevated_NeitherConfirmsNorRuns()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var serviceRunner = Substitute.For<IPowerShellRunner>();
            var vm = NewVm(serviceRunner: serviceRunner);

            await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

            Assert.Contains("administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            await serviceRunner.DidNotReceive().RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ---------- the repairs reach the right command line ----------

    [Fact]
    public async Task RunSfc_WhenElevated_LaunchesScannow()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.True(vm.IsElevated);

        await vm.RunSfcCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessAsync(
            "sfc.exe", "/scannow", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// DISM's argument string is asserted literally because one token in it is load-bearing:
    /// <c>/RestoreHealth</c> repairs, while the neighbouring <c>/StartComponentCleanup</c> — which
    /// lives on Quick Cleanup — permanently discards the ability to uninstall installed updates.
    /// </summary>
    [Fact]
    public async Task RunDism_WhenElevated_LaunchesRestoreHealth()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunDismCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessAsync(
            "DISM.exe", "/Online /Cleanup-Image /RestoreHealth",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task NoRepair_PassesResetBase()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunDismCommand.ExecuteAsync(null);

        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Is<string>(a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    // ---------- re-entry ----------

    [Fact]
    public async Task RunSfc_WhenAlreadyRunning_ReturnsImmediatelyWithoutChangingStatus()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        vm.IsSfcRunning = true;
        vm.StatusMessage = "marker";

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
        Assert.True(vm.IsSfcRunning); // left as the caller set it
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RunDism_WhenAlreadyRunning_ReturnsImmediatelyWithoutChangingStatus()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        vm.IsDismRunning = true;
        vm.StatusMessage = "marker";

        await vm.RunDismCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
        Assert.True(vm.IsDismRunning);
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    // ---------- console wiring ----------

    /// <summary>
    /// Both runners feed the one console: the service's own stream, and the one SFC and DISM drive
    /// directly. They are separate instances because <see cref="IPowerShellRunner"/> is registered
    /// Transient, so a test that only checked one would miss half the wiring.
    /// </summary>
    [Fact]
    public void BothRunnerStreams_ReachTheConsole()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner, serviceRunner);

        runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("from the repair runner"));
        serviceRunner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("from the service runner"));

        var text = vm.Console.Lines.Select(l => l.Text).ToList();
        Assert.Contains("from the repair runner", text);
        Assert.Contains("from the service runner", text);
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("_cts")]
    [InlineData("_sfcCts")]
    [InlineData("_dismCts")]
    public void CancelCommand_CancelsEveryRepairsTokenSource(string fieldName)
    {
        var vm = NewVm();
        var field = typeof(SystemFixesViewModel)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var cts = new CancellationTokenSource();
        field.SetValue(vm, cts);

        vm.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_UnsubscribesBothRunnerStreams()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner, serviceRunner);

        vm.Dispose();

        runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("after dispose"));
        serviceRunner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("after dispose"));

        Assert.DoesNotContain("after dispose", vm.Console.Lines.Select(l => l.Text));
    }
}

// ---------- SFC result parsing ----------

public class SfcResultParsingTests
{
    [Fact]
    public void ParseSfcResult_NoViolations_ReturnsGreen()
    {
        var lines = new[] { "Windows Resource Protection did not find any integrity violations." };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("No integrity violations", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseSfcResult_SuccessfullyRepaired_ReturnsYellow()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files and successfully repaired them." };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully repaired", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_UnableToFix_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files but was unable to fix some of them." };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not repair", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_CouldNotPerform_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection could not perform the requested operation." };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not run", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseSfcResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult(lines, 1);
        Assert.Contains("exit code 1", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_EmptyLines_FallsBackToExitCode()
    {
        var (verdict, color) = SystemFixesViewModel.ParseSfcResult([], 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }
}

// ---------- DISM result parsing ----------

/// <summary>
/// Serialized because <see cref="SystemModificationLock_IsMutuallyExclusive"/> takes the process-wide
/// <see cref="OperationLockService"/> and asserts which operation holds it. The attribute used to sit
/// on the neighbouring view-model class instead, which left this one — the class that actually takes
/// the lock — running in parallel with every other class that takes it.
/// </summary>
[Collection("ProcessWideStatics")]
public class DismResultParsingTests
{
    [Fact]
    public void ParseDismResult_RestoreSuccessful_ReturnsGreen()
    {
        var lines = new[] { "The restore operation completed successfully." };
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 0);
        Assert.Contains("healthy", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseDismResult_CorruptionRepaired_ReturnsYellow()
    {
        var lines = new[] { "The component store corruption was repaired." };
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 0);
        Assert.Contains("repaired", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseDismResult_SourceNotFound_ReturnsRed()
    {
        var lines = new[] { "The source files could not be found." };
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 0);
        Assert.Contains("source files", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseDismResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseDismResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 87);
        Assert.Contains("exit code 87", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    // SFC and DISM both acquire the SystemModification operation lock (after the elevation gate) so
    // they are mutually exclusive — with each other, and with Quick Cleanup's component-store
    // operations, which run DISM against the same online image. This pins the mutual-exclusion
    // contract at the service level, where it holds without elevation.
    [Fact]
    public void SystemModificationLock_IsMutuallyExclusive()
    {
        using var first = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(first);

        // A second acquire for the same category (e.g. DISM while SFC holds it) must fail.
        var second = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        Assert.Null(second);
        Assert.Equal("SFC scan", OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification));

        first!.Dispose();
        // Once released, the category is free again.
        using var third = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        Assert.NotNull(third);
    }
}
