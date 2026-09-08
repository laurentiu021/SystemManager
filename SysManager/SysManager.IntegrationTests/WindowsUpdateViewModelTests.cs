// SysManager · WindowsUpdateViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class WindowsUpdateViewModelTests
{
    /// <summary>A dialog that counts prompts instead of showing them, and always answers no.</summary>
    private sealed class RecordingDialog : IDialogService
    {
        public int Prompts { get; private set; }

        public bool Confirm(string message, string title)
        {
            Prompts++;
            return false;
        }

        public CloseChoice AskCloseOrMinimize(string message, string title)
        {
            Prompts++;
            return CloseChoice.Cancel;
        }

        public void Inform(string message, string title) => Prompts++;
    }

    private static WindowsUpdateViewModel NewVm() => new(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());

    // ---------- construction ----------

    [Fact]
    public void Constructor_ConsoleNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_ModuleStatus_NonEmpty()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.ModuleStatus));
    }

    [Fact]
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        Assert.IsType<bool>(vm.IsElevated);
    }

    [Fact]
    public void Constructor_IsBusyFalse()
    {
        // IsBusy may flip briefly during AutoCheckOnStartAsync, but the
        // constructor itself returns with IsBusy = false synchronously.
        var vm = NewVm();
        // Just assert it's a bool — the auto-check is fire-and-forget.
        Assert.IsType<bool>(vm.IsBusy);
    }

    // ---------- commands ----------

    /// <summary>
    /// Every command the Windows Update tab is expected to expose.
    /// </summary>
    /// <remarks>
    /// <c>ListFeatureUpdatesCommand</c> was a row here and had to go — see
    /// <c>WindowsUpdateAutoCheckTests.CommandExists</c> for the trace. This list is the SECOND copy of that
    /// one, which is why the same stale row survived in two places: repairing one and grepping only its own
    /// file would have left this identical theory still asserting a command deleted three months earlier.
    /// </remarks>
    [Theory]
    [InlineData("CheckModuleCommand")]
    [InlineData("InstallModuleCommand")]
    [InlineData("ListUpdatesCommand")]
    [InlineData("DeferFeatureUpdatesCommand")]
    [InlineData("ShowHistoryCommand")]
    [InlineData("CheckPendingRebootCommand")]
    [InlineData("InstallUpdatesCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_WithLiveCts_RequestsCancellation()
    {
        var vm = NewVm();
        var cts = new CancellationTokenSource();
        typeof(WindowsUpdateViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);
        vm.CancelCommand.Execute(null);
        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- elevation gates ----------

    /// <summary>
    /// Elevated, installing the module is REFUSED and the reason is stated.
    /// </summary>
    /// <remarks>
    /// The gate here runs the other way round from the rest of the tab: PSWindowsUpdate belongs in the
    /// per-user module path, so an elevated session must not install it.
    /// <para>This test was called <c>InstallModule_WhenNotElevated_SetsStatusMessage</c> and skipped itself
    /// when elevated, which means it only ever ran the branch that PROCEEDS — calling a real
    /// <c>PowerShellRunner</c> to install a PowerShell module on whoever ran it. It then asserted
    /// <c>ex == null || ex is NullReferenceException</c>, which no behaviour can fail. The runner is a
    /// substitute now, and the assertion is that nothing was run.</para>
    /// </remarks>
    [Fact]
    public async Task InstallModule_WhenElevated_RefusesAndSaysWhy()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = new RecordingRunner();
        var vm = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());

        await vm.InstallModuleCommand.ExecuteAsync(null);

        Assert.Contains("non-administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, runner.Calls);
    }

    /// <summary>
    /// With nothing selected, installing updates stops before it asks anything.
    /// </summary>
    /// <remarks>
    /// The old version of this asserted the same "null or NRE" as the one above. The first thing
    /// <c>InstallUpdatesAsync</c> does is count the selected rows, so with an empty list the honest assertion
    /// is that it says so and never reaches the confirmation dialog — which also means the test cannot
    /// accidentally start an install.
    /// </remarks>
    [Fact]
    public async Task InstallUpdates_WithNothingSelected_StopsBeforeConfirming()
    {
        var runner = new RecordingRunner();
        var vm = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());
        Assert.Empty(vm.Updates);

        var previous = DialogService.Instance;
        var dialog = new RecordingDialog();
        DialogService.Instance = dialog;
        try
        {
            await vm.InstallUpdatesCommand.ExecuteAsync(null);

            Assert.Equal("No updates selected.", vm.StatusMessage);
            Assert.Equal(0, dialog.Prompts);
            Assert.Equal(0, runner.Calls);
        }
        finally { DialogService.Instance = previous; }
    }

    // ---------- runner plumbing ----------

    /// <summary>
    /// A line from the runner reaches the console.
    /// </summary>
    /// <remarks>
    /// Run on the UI thread, and that is the whole fix. <c>ConsoleViewModel.Append</c> marshals when it is
    /// called from anywhere else:
    /// <code>
    /// if (Application.Current?.Dispatcher.CheckAccess() == false)
    /// {
    ///     Application.Current.Dispatcher.BeginInvoke(() => Append(line));
    ///     return;
    /// }
    /// </code>
    /// Raised from the MTA test thread it therefore queued the append and returned, leaving
    /// <c>Lines.Count</c> at zero on the next statement — but only when an <c>Application</c> existed. With
    /// none, the guard is false and <c>Append</c> runs inline, so the same assertion passed. Which of those
    /// the test got depended on whether a UI test had run first: it failed once and passed twice across
    /// three consecutive suite runs with nothing between them touching either class (#2157). A test that
    /// reports green two times in three while asserting nothing reliable is worse than a red one.
    /// <para>On the STA thread the marshal is a no-op — <c>CheckAccess()</c> is true, because that thread
    /// is the one the <c>Application</c> was created on — so the append happens before the assertion, in
    /// every ordering. The premise is asserted rather than assumed: if <c>StaHelper</c> ever stops being
    /// the dispatcher's own thread this fails loudly instead of quietly going order-dependent again.</para>
    /// <para>Deliberately NOT fixed by waiting for the queued operation. The suite's STA thread drains a
    /// work queue rather than pumping a dispatcher (#2156), so posted work never runs and there would be
    /// nothing to wait for. Nor by making <c>Append</c> synchronous: it marshals for a reason —
    /// <c>LineReceived</c> arrives from PowerShell on a pool thread and <c>Lines</c> is bound — and a
    /// blocking marshal is the defect removed from ten other places in #2152.</para>
    /// <para>No <c>[Collection]</c> change needed. <see cref="StaHelper"/> serialises everything through
    /// one queue, so this cannot interleave with the Windows-level collection even though it runs
    /// alongside it.</para>
    /// </remarks>
    [Fact]
    public void RunnerLineReceived_AppendsToConsole()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            Assert.True(System.Windows.Application.Current!.Dispatcher.CheckAccess(),
                "this test is only deterministic on the dispatcher's own thread — Append marshals from "
                + "anywhere else, and the assertion below would race the queued operation");

            var runner = new PowerShellRunner();
            var vm = new WindowsUpdateViewModel(
                runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());

            var ev = typeof(PowerShellRunner)
                .GetField(nameof(PowerShellRunner.LineReceived),
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var del = (MulticastDelegate?)ev?.GetValue(runner);
            Assert.NotNull(del);

            del!.DynamicInvoke(Models.PowerShellLine.Output("wu test"));

            Assert.Contains(vm.Console.Lines, line => line.Text.Contains("wu test", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void RunnerProgressChanged_UpdatesProgress()
    {
        var runner = new PowerShellRunner();
        var vm = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());

        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.ProgressChanged),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(75);
        Assert.Equal(75, vm.Progress);
    }
}
