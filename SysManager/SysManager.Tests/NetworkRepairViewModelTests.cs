// SysManager · NetworkRepairViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class NetworkRepairViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new NetworkRepairViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotRepairing()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new NetworkRepairViewModel(shared);
        Assert.False(vm.IsRepairing);
        Assert.Equal("", vm.RepairStatus);
        Assert.False(vm.RepairNeedsReboot);
    }
}
