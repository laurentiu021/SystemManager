// SysManager · SpeedTestViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class SpeedTestViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState();
        var vm = new SpeedTestViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotTesting()
    {
        var shared = new NetworkSharedState();
        var vm = new SpeedTestViewModel(shared);
        Assert.False(vm.IsSpeedTesting);
        Assert.False(vm.IsHttpTesting);
        Assert.False(vm.IsOoklaTesting);
        Assert.Equal(0, vm.SpeedProgress);
    }

    [Fact]
    public void HttpResult_DefaultNull()
    {
        var shared = new NetworkSharedState();
        var vm = new SpeedTestViewModel(shared);
        Assert.Null(vm.HttpResult);
    }

    [Fact]
    public void OoklaResult_DefaultNull()
    {
        var shared = new NetworkSharedState();
        var vm = new SpeedTestViewModel(shared);
        Assert.Null(vm.OoklaResult);
    }

    [Fact]
    public void CancelSpeedCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState();
        var vm = new SpeedTestViewModel(shared);
        vm.CancelSpeedCommand.Execute(null);
    }
}
