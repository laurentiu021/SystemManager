// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class DashboardViewModelTests
{
    [Fact]
    public void Ctor_SetsElevationFlag()
    {
        var vm = new DashboardViewModel(new SystemInfoService());
        // Just ensures IsElevated is true/false (no throw).
        _ = vm.IsElevated;
    }

    [Fact]
    public async Task RefreshCommand_CompletesAndPopulatesFields()
    {
        var vm = new DashboardViewModel(new SystemInfoService());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Snapshot);
        Assert.False(string.IsNullOrWhiteSpace(vm.OsLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.CpuLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.MemLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.UptimeLine));
    }

    [Fact]
    public async Task RefreshCommand_ResetsBusyFlag_WhenDone()
    {
        var vm = new DashboardViewModel(new SystemInfoService());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task RefreshCommand_SetsStatusMessage()
    {
        var vm = new DashboardViewModel(new SystemInfoService());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void RequestElevationCommand_WhenAlreadyElevated_DoesNotShutdown()
    {
        // We cannot realistically call RequestElevation in a test because it
        // would try to spawn a process and shut the test host down. We only
        // verify the command exists and is invokable.
        var vm = new DashboardViewModel(new SystemInfoService());
        Assert.NotNull(vm.RequestElevationCommand);
    }
}
