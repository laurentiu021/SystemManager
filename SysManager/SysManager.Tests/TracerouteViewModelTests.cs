// SysManager · TracerouteViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class TracerouteViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState();
        var vm = new TracerouteViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultTraceHost_Is8888()
    {
        var shared = new NetworkSharedState();
        var vm = new TracerouteViewModel(shared);
        Assert.Equal("8.8.8.8", vm.TraceHost);
    }

    [Fact]
    public void IsTracing_DefaultFalse()
    {
        var shared = new NetworkSharedState();
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void IsAutoTraceRunning_DefaultFalse()
    {
        var shared = new NetworkSharedState();
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsAutoTraceRunning);
    }

    [Fact]
    public void CancelTraceCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState();
        var vm = new TracerouteViewModel(shared);
        vm.CancelTraceCommand.Execute(null);
    }
}
