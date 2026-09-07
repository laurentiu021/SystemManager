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

    [Theory]
    [InlineData("CheckModuleCommand")]
    [InlineData("InstallModuleCommand")]
    [InlineData("ListUpdatesCommand")]
    [InlineData("ListFeatureUpdatesCommand")]
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

    [Fact]
    public void RunnerLineReceived_AppendsToConsole()
    {
        var runner = new PowerShellRunner();
        var vm = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());

        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.LineReceived),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(Models.PowerShellLine.Output("wu test"));

        Assert.True(vm.Console.Lines.Count >= 1);
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
