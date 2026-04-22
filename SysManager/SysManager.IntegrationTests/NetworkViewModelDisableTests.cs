// SysManager · NetworkViewModelDisableTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Regression tests for the "I unchecked a target but it keeps pinging" issue.
/// </summary>
[Collection("Network")]
public class NetworkViewModelDisableTests
{
    private static void InvokeOnSample(NetworkViewModel vm, PingSample sample)
    {
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object[] { sample });
    }

    [Fact]
    public void DisabledTarget_DoesNotUpdateStatsOrBuffer()
    {
        var vm = new NetworkViewModel();
        var target = vm.Targets.First(t => t.Host == "8.8.8.8");
        target.IsEnabled = false;

        // Simulate the ping pump emitting a sample for the disabled target.
        // This CAN legitimately happen if a ping was in flight when the user
        // unchecked the target — we must ignore it at the VM layer too.
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 42, "OK"));

        Assert.Null(target.LastLatencyMs);
        Assert.Null(target.AverageMs);
        Assert.Equal(0, target.LossPercent);
    }

    [Fact]
    public void ReEnablingTarget_ResumesProcessingSamples()
    {
        var vm = new NetworkViewModel();
        var target = vm.Targets.First(t => t.Host == "1.1.1.1");
        target.IsEnabled = false;
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", 99, "OK"));
        Assert.Null(target.LastLatencyMs);

        target.IsEnabled = true;
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", 10, "OK"));
        Assert.Equal(10, target.LastLatencyMs);
    }

    [Fact]
    public void DisablingTarget_ClearsChartBuffer()
    {
        var vm = new NetworkViewModel();
        var target = vm.Targets.First(t => t.Host == "8.8.8.8");

        // Populate with a few samples while enabled.
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 10, "OK"));
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 12, "OK"));
        Assert.NotNull(target.LastLatencyMs);

        // Now disable — stats must reset and the chart buffer must be empty.
        target.IsEnabled = false;
        Assert.Null(target.LastLatencyMs);
        Assert.Null(target.AverageMs);
        Assert.Equal(0, target.LossPercent);
        Assert.Equal("—", target.Status);
    }

    [Fact]
    public void RemovingCustomTarget_RemovesFromPingerAndCharts()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "203.0.113.9";
        vm.AddCustomTargetCommand.Execute(null);
        var custom = vm.Targets.Last();
        Assert.True(custom.IsCustom);
        var beforeCount = vm.Targets.Count;
        var beforeSeries = vm.LatencySeries.Count;

        vm.RemoveTargetCommand.Execute(custom);
        Assert.Equal(beforeCount - 1, vm.Targets.Count);
        Assert.Equal(beforeSeries - 1, vm.LatencySeries.Count);
    }
}
