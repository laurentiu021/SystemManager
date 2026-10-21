// SysManager · SystemFixesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
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
    // SFC's sentences as English Windows prints them: reading this Windows' sfc.exe is the integration suite's job.
    private static SystemFixesViewModel NewVm(
        IPowerShellRunner? runner = null, IPowerShellRunner? serviceRunner = null) =>
        new(new SystemFixService(serviceRunner ?? Substitute.For<IPowerShellRunner>()),
            runner ?? Substitute.For<IPowerShellRunner>())
        {
            SfcSentences = static () => SfcOutput.English,
        };

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
    /// The mirror image, and the reason the two shell fixes exist as a separate gate: they must be
    /// clickable WITHOUT elevation.
    /// </summary>
    /// <remarks>
    /// Explorer is the user's own process and the icon caches live under their profile, so requiring
    /// admin would put the tab's cheapest, most common fixes behind a UAC prompt they do not need
    /// (#1490). This cannot be asserted in the UI suite: the CI runner is elevated, so every button there
    /// is enabled and the claim would be vacuous. `ForceElevation(false)` is what makes it mean something.
    /// </remarks>
    [Theory]
    [InlineData("RestartExplorerCommand")]
    [InlineData("RebuildIconCacheCommand")]
    public void EveryShellFix_IsClickableWithoutElevation(string name)
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm();
        Assert.False(vm.IsElevated, "the scope must reach the view-model's constructor");

        var command = (System.Windows.Input.ICommand)vm.GetType().GetProperty(name)!.GetValue(vm)!;
        Assert.True(command.CanExecute(null),
            $"{name} is gated on elevation. It runs as the user, so a user without administrator rights "
            + "would see it disabled and conclude the fix does not work.");
    }

    /// <summary>
    /// And they still respect the tab's one-repair-at-a-time rule, so a shell restart cannot start
    /// while SFC is grinding.
    /// </summary>
    [Theory]
    [InlineData("RestartExplorerCommand")]
    [InlineData("RebuildIconCacheCommand")]
    public void EveryShellFix_IsBlockedWhileAnotherRepairRuns(string name)
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm();
        var command = (System.Windows.Input.ICommand)vm.GetType().GetProperty(name)!.GetValue(vm)!;
        Assert.True(command.CanExecute(null));

        vm.IsSfcRunning = true;

        Assert.False(command.CanExecute(null));
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
        using var dialog = new DialogAnswer(confirm: true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.True(vm.IsElevated);

        await vm.RunSfcCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessAsync(
            "sfc.exe", "/scannow", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task Escape_StopsNoRepair_WhileTheCancelButtonStillDoes()
    {
        // Escape ended SFC half-way from a bare keypress (#2834). The Cancel button is the way to stop a repair.
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sfcToken = default;
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("sfc.exe", "/scannow", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  sfcToken = call.ArgAt<CancellationToken>(2);
                  sfcToken.Register(() => running.TrySetCanceled(sfcToken));
                  return running.Task;
              });
        var vm = NewVm(runner);

        var sfc = vm.RunSfcCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsBusy);
            Assert.Null(vm.EscapeCancel);

            vm.CancelCommand.Execute(null);
            Assert.True(sfcToken.IsCancellationRequested);
        }
        finally
        {
            running.TrySetResult(0);
            await sfc;
        }
    }

    [Fact]
    public async Task RunSfc_ReadsSfcsOwnOutput_ForItsProgressAndItsVerdict()
    {
        // Redirected, SFC writes UTF-16, and the tab read it as the OEM code page: a NUL between every letter, so the
        // bar never showed a percentage and the verdict was the exit code's green "Scan completed successfully."
        // whatever SFC said (#2835). The runner decodes SFC's bytes with the encoding the tab asks for.
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("sfc.exe", "/scannow", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
            .Returns(call =>
            {
                RaiseEachLine(runner, SfcOutputAsWritten(), call.ArgAt<System.Text.Encoding?>(3));
                return 0;
            });
        var vm = NewVm(runner);
        List<int> progress = [];
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Progress)) progress.Add(vm.Progress);
        };

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Equal("No integrity violations found — your system files are healthy.", vm.SfcVerdict);
        Assert.Equal(StatusColors.Good, vm.SfcVerdictColorHex);
        Assert.True(progress is [.., 100] && progress.Count == 100,
            $"the bar took {progress.Count} values, ending at {(progress.Count > 0 ? progress[^1] : "none")}");
    }

    /// <summary>
    /// The start and end of a real scan's output, as SFC writes it redirected: UTF-16LE, a line ended with "\r\r\n",
    /// and the progress line written over with a bare "\r", 0 to 100.
    /// </summary>
    private static byte[] SfcOutputAsWritten() => System.Text.Encoding.Unicode.GetBytes(
        "\r\r\nBeginning system scan.  This process will take some time.\r\r\n\r\r\n"
        + "Beginning verification phase of system scan.\r\r\n"
        + string.Concat(Enumerable.Range(0, 101).Select(percent => $"\rVerification {percent}% complete."))
        + "\r\n\r\r\nWindows Resource Protection did not find any integrity violations.\r\r\n");

    // As PowerShellRunner hands on a process's output: a line at a time, the blank ones left out.
    private static void RaiseEachLine(IPowerShellRunner runner, byte[] output, System.Text.Encoding? encoding)
    {
        using var reader = new StreamReader(new MemoryStream(output), encoding ?? System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(line));
        }
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
        using var dialog = new DialogAnswer(confirm: true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunDismCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessAsync(
            "DISM.exe", "/Online /English /Cleanup-Image /RestoreHealth",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task NoRepair_PassesResetBase()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunDismCommand.ExecuteAsync(null);

        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Is<string>(a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    // ---------- a fix that stops says why (#2875) ----------

    private static SystemFixesViewModel VmWhoseFixStops(string reason)
    {
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        serviceRunner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                     .Returns<Collection<PSObject>>(_ => throw new RuntimeException(reason));
        return NewVm(serviceRunner: serviceRunner);
    }

    [Fact]
    public async Task AFixThatStops_SaysWhy_InTheConsoleAndTheStatusLine()
    {
        // The reason is the script's own, raised as the run's exception, so it never reached the console the status
        // line pointed at: "see the output for details" over an empty console.
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        const string Reason = "App Installer package not found. Install \"App Installer\" from the Microsoft Store.";
        var vm = VmWhoseFixStops(Reason);

        await vm.ReinstallWinGetCommand.ExecuteAsync(null);

        Assert.Equal($"Reinstall WinGet did not complete: {Reason}", vm.StatusMessage);
        Assert.Contains(vm.Console.Lines, l => l.Kind == OutputKind.Error && l.Text == Reason);
    }

    [Fact]
    public async Task AFixThatStops_PutsOnlyTheFirstLineOfTheReasonInTheStatusLine()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = VmWhoseFixStops("Access is denied.\r\nAt line:3 char:5");

        await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

        Assert.Equal("Reset Windows Update did not complete: Access is denied.", vm.StatusMessage);
        Assert.Contains(vm.Console.Lines, l => l.Kind == OutputKind.Error && l.Text.EndsWith("char:5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFixThatStopsWithNoReason_DoesNotPointAtAnEmptyConsole()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = VmWhoseFixStops("");

        await vm.ReinstallWinGetCommand.ExecuteAsync(null);

        Assert.Equal("Reinstall WinGet did not complete.", vm.StatusMessage);
        Assert.DoesNotContain(vm.Console.Lines, l => l.Kind == OutputKind.Error);
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

    // ---------- Reset Windows Update and the system-modification lock (#2484) ----------

    [Fact]
    public async Task ResetWindowsUpdate_WhileAnotherSystemChangeRuns_DoesNotRun()
    {
        // The reset force-stops the Windows Update services and renames their caches, and it could run in the
        // middle of an install from the Windows Update tab, which then failed.
        using var elevated = AdminHelper.ForceElevation(true);
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(serviceRunner: serviceRunner);
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Windows Update install");
        Assert.NotNull(held);

        await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

        await serviceRunner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
        Assert.Equal("Cannot start — Windows Update install is already running.", vm.StatusMessage);
        Assert.False(vm.IsFixRunning);
    }

    // ---------- Reset Windows Update and the install lock (#2553) ----------

    [Fact]
    public async Task ResetWindowsUpdate_WhileAnAppInstallRuns_DoesNotRun()
    {
        // The reset stops Windows Installer, which cuts off an app install, upgrade or uninstall in the middle. It took
        // only the system-modification lock, so it started under one that App Updates, Bulk Installer, Uninstaller or
        // Update All Apps was running.
        using var elevated = AdminHelper.ForceElevation(true);
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(serviceRunner: serviceRunner);
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Install, "App Updates");
        Assert.NotNull(held);

        await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

        await serviceRunner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
        Assert.Equal("Cannot start — App Updates is already running.", vm.StatusMessage);
        Assert.False(vm.IsFixRunning);
        // The system-modification lock it took first goes back with the refusal; a kept one would block every
        // repair, feature change and update install until SysManager restarted.
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification));
    }

    [Fact]
    public async Task ResetWindowsUpdate_HoldsTheInstallLockWhileItRuns()
    {
        // The other direction: while the reset has Windows Installer stopped, an install started on another tab is
        // refused rather than started into a stopped service.
        using var elevated = AdminHelper.ForceElevation(true);
        string? heldBy = null;
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        serviceRunner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                heldBy = OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install);
                return Task.FromResult(new Collection<PSObject>());
            });
        var vm = NewVm(serviceRunner: serviceRunner);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

        Assert.Equal("Windows Update reset", heldBy);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install));
    }

    [Fact]
    public async Task ReinstallWinGet_TakesNoSystemLock()
    {
        // The other half. Re-registering WinGet for the user conflicts with nothing that holds this lock, so an
        // SFC scan running in the background must not stop it.
        using var elevated = AdminHelper.ForceElevation(true);
        var serviceRunner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(serviceRunner: serviceRunner);
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);

        await vm.ReinstallWinGetCommand.ExecuteAsync(null);

        await serviceRunner.ReceivedWithAnyArgs(1).RunAsync(default!, default, default);
    }

    // ---------- every repair asks first (#2505) ----------
    // The tab says each fix asks for confirmation before it runs. SFC and DISM started straight away.

    [Fact]
    public async Task RunSfc_AsksFirst_AndChangesNothingWhenDeclined()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: false);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Contains("replaces the damaged ones", Assert.Single(dialog.Messages), StringComparison.Ordinal);
        Assert.Equal("Cancelled.", vm.StatusMessage);
        Assert.False(vm.IsSfcRunning);
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RunDism_AsksFirst_AndChangesNothingWhenDeclined()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: false);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);

        await vm.RunDismCommand.ExecuteAsync(null);

        Assert.Contains("needs an internet connection", Assert.Single(dialog.Messages), StringComparison.Ordinal);
        Assert.Equal("Cancelled.", vm.StatusMessage);
        Assert.False(vm.IsDismRunning);
        await runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task ResetWindowsUpdate_Confirmation_NamesWindowsInstallerAndTheKeptFolders()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var dialog = new DialogAnswer(confirm: false);
        var vm = NewVm();

        await vm.ResetWindowsUpdateCommand.ExecuteAsync(null);

        var message = Assert.Single(dialog.Messages);
        Assert.Contains("Stopping Windows Installer interrupts any installation", message, StringComparison.Ordinal);
        Assert.Contains("The old folders are kept", message, StringComparison.Ordinal);
        Assert.Contains("SysManager does not remove them", message, StringComparison.Ordinal);
    }
}

// ---------- SFC result parsing ----------

/// <summary>
/// The verdict on SFC's output, read by the first lines of SFC's own messages. These give the English ones; the real
/// message table is read in the integration suite's <c>SfcMessageTableTests</c>.
/// </summary>
public class SfcResultParsingTests
{
    private static (string Verdict, string ColorHex) Parse(int exitCode, params string[] lines) =>
        SystemFixesViewModel.ParseSfcResult(lines, exitCode, SfcOutput.English);

    private static string Sentence(SfcOutcome outcome) =>
        SfcOutput.Messages.Single(m => m.Outcome == outcome).English;

    [Fact]
    public void EveryOutcome_HasOneVerdict_WorstFirst() =>
        Assert.Equal(Enum.GetValues<SfcOutcome>(), SystemFixesViewModel.SfcVerdicts.Select(v => v.Outcome));

    [Fact]
    public void ParseSfcResult_NoViolations_ReturnsGreen()
    {
        var (verdict, color) = Parse(0, Sentence(SfcOutcome.NoViolations));
        Assert.Contains("No integrity violations", verdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseSfcResult_SuccessfullyRepaired_ReturnsYellow()
    {
        var (verdict, color) = Parse(0, Sentence(SfcOutcome.Repaired));
        Assert.Equal("Corrupted files were found and successfully repaired.", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_RepairedWithARestartToFinish_SaysToRestart()
    {
        var (verdict, color) = Parse(0, Sentence(SfcOutcome.Repaired), Sentence(SfcOutcome.TakesEffectAfterRestart));
        Assert.Equal(
            "Corrupted files were found and successfully repaired. Restart Windows to finish the repair.", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_UnableToFix_ReturnsRed()
    {
        var (verdict, color) = Parse(0, Sentence(SfcOutcome.UnableToFix));
        Assert.Contains("could not repair", verdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_CouldNotPerform_ReturnsRed()
    {
        var (verdict, color) = Parse(0, Sentence(SfcOutcome.CouldNotPerform));
        Assert.Contains("could not run", verdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_NoViolationsButCorruptMetadata_IsNotCalledHealthy()
    {
        // The message starts as the healthy one does, and the English phrase the tab matched found it in there: a
        // store too damaged to check against ended in a green "your system files are healthy". These are its three
        // lines as SFC prints them.
        var (verdict, color) = Parse(0,
            "Windows Resource Protection did not find any integrity violations but cannot",
            "guarantee the integrity of the system because component metadata is corrupt.",
            "Please run \"dism [/Online | /Image] /cleanup-image /restorehealth\" and try again.");
        Assert.Contains("DISM /RestoreHealth", verdict, StringComparison.Ordinal);
        Assert.DoesNotContain("healthy", verdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Theory]
    [InlineData(nameof(SfcOutcome.ServiceDidNotStart), "Restart Windows", StatusColors.Bad)]
    [InlineData(nameof(SfcOutcome.AnotherOperationRunning), "Wait for it to finish", StatusColors.Warning)]
    [InlineData(nameof(SfcOutcome.RepairPending), "Restart Windows", StatusColors.Warning)]
    [InlineData(nameof(SfcOutcome.MaximumQueued), "Restart Windows", StatusColors.Warning)]
    [InlineData(nameof(SfcOutcome.MaximumRepaired), "Run SFC again", StatusColors.Warning)]
    [InlineData(nameof(SfcOutcome.TakesEffectAfterRestart), "Restart Windows", StatusColors.Warning)]
    public void ParseSfcResult_EveryOtherEnding_SaysWhatToDoNext(string outcome, string advice, string expected)
    {
        var (verdict, color) = Parse(0, Sentence(Enum.Parse<SfcOutcome>(outcome)));
        Assert.Contains(advice, verdict, StringComparison.Ordinal);
        Assert.Equal(expected, color);
    }

    [Fact]
    public void ParseSfcResult_TheWorstEnding_Wins()
    {
        var (_, color) = Parse(0, Sentence(SfcOutcome.Repaired), Sentence(SfcOutcome.UnableToFix));
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_ASentenceInAnotherLanguage_IsReadFromTheTableItIsGiven()
    {
        // Nothing here is English: a translated table's sentence is read, and with only that table, the English one
        // is not.
        var translated = new Dictionary<string, SfcOutcome>(StringComparer.Ordinal)
        {
            ["Alpha bravo charlie."] = SfcOutcome.UnableToFix,
        };
        Assert.Equal(StatusColors.Bad,
            SystemFixesViewModel.ParseSfcResult(["Alpha bravo charlie."], 0, translated).ColorHex);
        Assert.Equal(StatusColors.Neutral,
            SystemFixesViewModel.ParseSfcResult([Sentence(SfcOutcome.UnableToFix)], 0, translated).ColorHex);
    }

    [Fact]
    public void ParseSfcResult_ExitZeroNoMatch_IsNotCalledASuccess()
    {
        // Exit code 0 says SFC ran, not what it found.
        var (verdict, color) = Parse(0, "Some unrecognized output");
        Assert.Equal("Scan finished. Its result is in the console output.", verdict);
        Assert.Equal(StatusColors.Neutral, color);
    }

    [Fact]
    public void ParseSfcResult_NonZeroExit_ReturnsYellowFallback()
    {
        var (verdict, color) = Parse(1, "Some unrecognized output");
        Assert.Contains("exit code 1", verdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_EmptyLines_FallsBackToExitCode()
    {
        var (verdict, color) = Parse(0);
        Assert.Equal("Scan finished. Its result is in the console output.", verdict);
        Assert.Equal(StatusColors.Neutral, color);
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
    public void ParseDismResult_RestoreSuccessful_SaysTheStoreIsHealthyNow_AndToRunSfc()
    {
        // DISM ends a repair and a run that found nothing to repair with this one sentence, so the verdict cannot say
        // no repairs were needed (#2835). The banner, the bar and the last line are as DISM prints them; the sentence
        // before the last is the one DISM has for a restore, in CbsProvider.dll.mui.
        string[] lines =
        [
            "Deployment Image Servicing and Management tool",
            "[==========================100.0%==========================] ",
            "The restore operation completed successfully.",
            "The operation completed successfully.",
        ];
        var (verdict, color) = SystemFixesViewModel.ParseDismResult(lines, 0);
        Assert.Equal(
            "Windows' repair source is healthy now. Run SFC next to repair any damaged Windows files.", verdict);
        Assert.Equal(StatusColors.Good, color);
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
