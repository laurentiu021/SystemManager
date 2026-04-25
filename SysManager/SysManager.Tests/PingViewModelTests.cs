// SysManager · PingViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class PingViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState();
        var vm = new PingViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void ClearHistoryCommand_ResetsStats()
    {
        var shared = new NetworkSharedState();
        var vm = new PingViewModel(shared);
        vm.ClearHistoryCommand.Execute(null);
        Assert.All(shared.Targets, t => Assert.Null(t.LastLatencyMs));
    }

    [Fact]
    public void AddCustomTargetCommand_DelegatesToShared()
    {
        var shared = new NetworkSharedState();
        shared.NewTargetHost = "10.88.88.88";
        var vm = new PingViewModel(shared);
        var before = shared.Targets.Count;
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before + 1, shared.Targets.Count);
    }
}
