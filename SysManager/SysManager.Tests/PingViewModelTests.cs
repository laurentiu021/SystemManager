// SysManager · PingViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class PingViewModelTests
{
    /// <summary>
    /// Construction must wire the shared state AND leave the commands the view binds available.
    /// <para>This previously asserted only <c>Assert.Same(shared, vm.Shared)</c> — the argument
    /// handed to the constructor, read straight back — so it could fail only if that one assignment
    /// were deleted. A command left null would sail past it and surface as a dead button.</para>
    /// </summary>
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));

        var vm = new PingViewModel(shared);

        Assert.Same(shared, vm.Shared);
        Assert.NotNull(vm.ClearHistoryCommand);
        Assert.NotNull(vm.AddCustomTargetCommand);
    }

    /// <summary>
    /// Clearing must actually reset the per-target statistics.
    /// <para>This test used to run the command against a freshly built state — where every target's
    /// LastLatencyMs is ALREADY null — and then assert it was null. It asserted the initial state
    /// rather than any effect of the command, so it would have passed with the command's body
    /// deleted. It also leaned on Assert.All, which succeeds over an empty collection.</para>
    /// <para>It now dirties every field ClearHistory resets, so dropping any one of them fails
    /// here.</para>
    /// </summary>
    [Fact]
    public void ClearHistoryCommand_ResetsStats()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new PingViewModel(shared);

        Assert.NotEmpty(shared.Targets);   // otherwise the Assert.All below succeeds over nothing
        foreach (var t in shared.Targets)
        {
            t.LastLatencyMs = 42.5;
            t.AverageMs = 40.0;
            t.JitterMs = 3.25;
            t.LossPercent = 12;
            t.Status = "Online";
        }

        vm.ClearHistoryCommand.Execute(null);

        Assert.All(shared.Targets, t =>
        {
            Assert.Null(t.LastLatencyMs);
            Assert.Null(t.AverageMs);
            Assert.Null(t.JitterMs);
            Assert.Equal(0, t.LossPercent);
            Assert.Equal("—", t.Status);
        });
    }

    [Fact]
    public void AddCustomTargetCommand_DelegatesToShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        shared.NewTargetHost = "10.88.88.88";
        var vm = new PingViewModel(shared);
        var before = shared.Targets.Count;
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before + 1, shared.Targets.Count);
    }
}
