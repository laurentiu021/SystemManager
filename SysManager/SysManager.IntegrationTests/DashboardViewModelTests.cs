// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(
            sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true));
    }

    [Fact]
    public void Ctor_SetsElevationFlag()
    {
        var vm = NewVm();
        // Just ensures IsElevated is true/false (no throw).
        _ = vm.IsElevated;
    }

    [Fact]
    public async Task RefreshCommand_CompletesAndPopulatesFields()
    {
        var vm = NewVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.OsLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.UptimeLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.CpuName));
        Assert.True(vm.RamTotalGB >= 0);
    }

    [Fact]
    public async Task RefreshCommand_ResetsBusyFlag_WhenDone()
    {
        var vm = NewVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task RefreshCommand_SetsStatusMessage()
    {
        var vm = NewVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void RelaunchAsAdminCommand_Exists()
    {
        // We cannot realistically invoke RelaunchAsAdmin in a test because it
        // would try to spawn an elevated process and shut the test host down.
        // We only verify the command exists.
        var vm = NewVm();
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }
}
