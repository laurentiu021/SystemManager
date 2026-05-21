// SysManager · SpeedTestViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class SpeedTestViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotTesting()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.False(vm.IsSpeedTesting);
        Assert.False(vm.IsHttpTesting);
        Assert.False(vm.IsOoklaTesting);
        Assert.Equal(0, vm.SpeedProgress);
    }

    [Fact]
    public void HttpResult_DefaultNull()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.Null(vm.HttpResult);
    }

    [Fact]
    public void OoklaResult_DefaultNull()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.Null(vm.OoklaResult);
    }

    [Fact]
    public void CancelSpeedCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        vm.CancelSpeedCommand.Execute(null);
    }

    [Fact]
    public void HttpHistory_StartsEmpty()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.NotNull(vm.HttpHistory);
    }

    [Fact]
    public void OoklaHistory_StartsEmpty()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        Assert.NotNull(vm.OoklaHistory);
    }

    [Theory]
    [InlineData("RunHttpSpeedCommand")]
    [InlineData("RunOoklaSpeedCommand")]
    [InlineData("CancelSpeedCommand")]
    [InlineData("ClearHttpHistoryCommand")]
    [InlineData("ClearOoklaHistoryCommand")]
    public void CommandExists(string propertyName)
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public async Task ClearHttpHistoryCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        var ex = await Record.ExceptionAsync(() => vm.ClearHttpHistoryCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ClearOoklaHistoryCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new Services.SpeedTestHistoryService());
        var ex = await Record.ExceptionAsync(() => vm.ClearOoklaHistoryCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }
}
