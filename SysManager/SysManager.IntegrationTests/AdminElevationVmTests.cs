// SysManager · AdminElevationVmTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Verify that VMs which rely on elevation expose a deterministic
/// <c>IsElevated</c> flag and a <c>RelaunchAsAdminCommand</c> hooked up.
/// </summary>
public class AdminElevationVmTests
{
    [Fact]
    public void WindowsUpdateVm_ExposesIsElevated()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner());
        // Must equal the current process' elevation state.
        Assert.Equal(Helpers.AdminHelper.IsElevated(), vm.IsElevated);
    }

    [Fact]
    public void WindowsUpdateVm_HasRelaunchCommand()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner());
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }

    [Fact]
    public void CleanupVm_ExposesIsElevated()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.Equal(Helpers.AdminHelper.IsElevated(), vm.IsElevated);
    }

    [Fact]
    public void CleanupVm_HasRelaunchCommand()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }

    [Fact]
    public void AppUpdatesVm_ExposesIsElevated()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        Assert.Equal(Helpers.AdminHelper.IsElevated(), vm.IsElevated);
    }

    [Fact]
    public void AppUpdatesVm_HasRelaunchCommand()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }

    [Fact]
    public void AllElevationVms_Report_SameElevationFlag()
    {
        var a = new WindowsUpdateViewModel(new PowerShellRunner()).IsElevated;
        var b = new CleanupViewModel(new PowerShellRunner()).IsElevated;
        var c = new AppUpdatesViewModel(new WingetService(new PowerShellRunner())).IsElevated;
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }
}
