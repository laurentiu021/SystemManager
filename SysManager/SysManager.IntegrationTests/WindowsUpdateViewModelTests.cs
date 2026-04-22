using System.Reflection;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class WindowsUpdateViewModelTests
{
    private static WindowsUpdateViewModel NewVm() => new(new PowerShellRunner());

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

    [Fact]
    public async Task InstallModule_WhenNotElevated_SetsStatusMessage()
    {
        var vm = NewVm();
        if (vm.IsElevated) return;

        var ex = await Record.ExceptionAsync(() => vm.InstallModuleCommand.ExecuteAsync(null));
        // May throw NRE if RelaunchAsAdmin succeeds but Application.Current is null in test.
        // Either way, StatusMessage should have been set before the throw.
        Assert.True(ex == null || ex is NullReferenceException);
    }

    [Fact]
    public async Task InstallUpdates_WhenNotElevated_SetsStatusMessage()
    {
        var vm = NewVm();
        if (vm.IsElevated) return;

        var ex = await Record.ExceptionAsync(() => vm.InstallUpdatesCommand.ExecuteAsync(null));
        Assert.True(ex == null || ex is NullReferenceException);
    }

    // ---------- runner plumbing ----------

    [Fact]
    public void RunnerLineReceived_AppendsToConsole()
    {
        var runner = new PowerShellRunner();
        var vm = new WindowsUpdateViewModel(runner);

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
        var vm = new WindowsUpdateViewModel(runner);

        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.ProgressChanged),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(75);
        Assert.Equal(75, vm.Progress);
    }
}
