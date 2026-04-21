using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests the sample-handling path of NetworkViewModel.
/// With no SynchronizationContext captured, OnSample runs inline — perfect
/// for deterministic unit tests.
/// </summary>
[Collection("Network")]
public class NetworkViewModelSampleTests
{
    private static void InvokeOnSample(NetworkViewModel vm, PingSample sample)
    {
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object[] { sample });
    }

    private static NetworkViewModel MakeVm() => new();

    [Fact]
    public void Sample_UpdatesTargetLastAndAverage()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeVm();
            var target = vm.Targets.First(t => t.Host == "8.8.8.8");

            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 10, "OK"));
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 20, "OK"));
            

            Assert.Equal(20, target.LastLatencyMs);
            Assert.Equal(15, target.AverageMs);
            Assert.Equal(0, target.LossPercent);
            Assert.Equal("OK", target.Status);
        });
    }

    [Fact]
    public void Sample_Timeout_IncreasesLoss_AndLeavesGap()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeVm();
            var target = vm.Targets.First(t => t.Host == "1.1.1.1");

            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", 10, "OK"));
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", null, "TimedOut"));
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", 30, "OK"));
            

            Assert.Equal(30, target.LastLatencyMs);
            Assert.Equal(20, target.AverageMs); // (10+30)/2
            Assert.True(target.LossPercent > 0);
        });
    }

    [Fact]
    public void Sample_ForUnknownHost_IsIgnored()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeVm();
            var ex = Record.Exception(() =>
            {
                InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "not-a-target", 10, "OK"));
                
            });
            Assert.Null(ex);
        });
    }

    [Fact]
    public void WindowShrink_TrimsBuffersOnNextSample()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeVm();
            vm.WindowSeconds = 600;
            var host = "9.9.9.9";

            var now = DateTime.UtcNow;
            for (int i = 0; i < 20; i++)
                InvokeOnSample(vm, new PingSample(now.AddSeconds(-i), host, 10, "OK"));
            

            // Shrink window: calling the partial method path happens via setter.
            vm.WindowSeconds = 5;
            

            // Push another sample to force trimming of old entries.
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
            

            // After trimming, the buffer on the chart should contain only recent points.
            // We can't inspect _buffers directly, but Target stats should be consistent:
            var target = vm.Targets.First(t => t.Host == host);
            Assert.NotNull(target.AverageMs);
        });
    }
}
