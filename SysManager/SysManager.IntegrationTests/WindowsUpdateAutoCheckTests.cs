// SysManager · WindowsUpdateAutoCheckTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class WindowsUpdateAutoCheckTests
{
    private static WindowsUpdateViewModel Build() => new(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());

    [Fact]
    public void ModuleStatus_HasInitialMessage()
    {
        var vm = Build();
        Assert.False(string.IsNullOrWhiteSpace(vm.ModuleStatus));
    }

    [Fact]
    public void ModuleStatus_DefersAvailabilityCheckUntilHistory()
    {
        var vm = Build();
        Assert.Contains("History", vm.ModuleStatus, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void IsElevated_IsBoolean()
    {
        var vm = Build();
        Assert.IsType<bool>(vm.IsElevated);
    }

    /// <summary>
    /// Every command the Windows Update tab is expected to expose.
    /// </summary>
    /// <remarks>
    /// <c>ListFeatureUpdatesCommand</c> was a row here and had to go: PR #609 ("Windows Update install never
    /// applied updates (fake success)", merged 2026-06-03) deleted <c>ListFeatureUpdatesAsync</c> from the
    /// view model and left this row behind, so the assertion failed for three months — invisibly, because
    /// this project was compile-checked in CI and never executed. The feature-updates command that DOES exist
    /// is <c>DeferFeatureUpdatesCommand</c>, and it is asserted below on its own merits rather than as a
    /// rename of the deleted one.
    /// <para>The identical list is duplicated in <c>WindowsUpdateViewModelTests.Command_IsExposedAndNotNull</c>,
    /// which is how one deleted command produced two failures — both copies name commands as STRINGS and look
    /// them up by reflection, so the compiler cannot see the reference at all. A third copy in
    /// <c>LightTouchViewModelTests.WindowsUpdateVm_AllCommandsExist</c> names the properties directly and
    /// never rotted: deleting a command would not have compiled. That is the difference worth noticing —
    /// a string-keyed assertion over a compiler-checkable thing buys nothing and hides a rename for months.</para>
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
    public void CommandExists(string propName)
    {
        var vm = Build();
        var p = vm.GetType().GetProperty(propName);
        Assert.NotNull(p);
        Assert.NotNull(p!.GetValue(vm));
    }

    [Fact]
    public void Console_Wired()
    {
        var vm = Build();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = Build();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void Progress_StartsZero()
    {
        var vm = Build();
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public void IsProgressIndeterminate_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var vm = Build();
        vm.ModuleAvailable = true;
        Assert.True(vm.ModuleAvailable);
        vm.ModuleStatus = "Available";
        Assert.Equal("Available", vm.ModuleStatus);
    }
}
