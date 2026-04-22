// SysManager · PingMonitorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests target the concurrency contract of PingMonitorService, NOT network
/// reachability. We use a non-routable TEST-NET host (192.0.2.1) so pings
/// complete fast (timeout) without requiring internet access.
/// </summary>
[Collection("Network")]
public class PingMonitorServiceTests
{
    private const string UnreachableHost = "192.0.2.1"; // RFC 5737 TEST-NET-1
    private const int ShortTimeoutMs = 300;

    private static PingMonitorService CreateFast()
        => new() { Interval = TimeSpan.FromMilliseconds(100), TimeoutMs = ShortTimeoutMs };

    [Fact]
    public void Start_WhenNotRunning_SetsIsRunning()
    {
        using var svc = CreateFast();
        Assert.False(svc.IsRunning);
        svc.Start();
        Assert.True(svc.IsRunning);
        svc.Stop();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void Start_CalledTwice_IsIdempotent()
    {
        using var svc = CreateFast();
        svc.Start();
        svc.Start(); // should not throw, should not spawn second loop
        Assert.True(svc.IsRunning);
        svc.Stop();
    }

    [Fact]
    public void Stop_WhenNotRunning_IsSafe()
    {
        using var svc = CreateFast();
        var ex = Record.Exception(() => svc.Stop());
        Assert.Null(ex);
    }

    [Fact]
    public void AddOrUpdate_Overwrites_ExistingHost()
    {
        using var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("A", "1.1.1.1", "#111"));
        svc.AddOrUpdate(new PingTarget("B", "1.1.1.1", "#222"));
        Assert.Single(svc.Targets);
        Assert.Equal("B", svc.Targets["1.1.1.1"].Name);
    }

    [Fact]
    public void Remove_UnknownHost_DoesNotThrow()
    {
        using var svc = CreateFast();
        var ex = Record.Exception(() => svc.Remove("does-not-exist"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task EmitsSamples_ForEnabledTargets()
    {
        using var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("Unreach", UnreachableHost, "#111"));

        var tcs = new TaskCompletionSource<PingSample>();
        svc.SampleReceived += s => tcs.TrySetResult(s);

        svc.Start();
        var sample = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        svc.Stop();

        Assert.Equal(UnreachableHost, sample.Host);
        // Unreachable: latency must be null, status != OK
        Assert.Null(sample.LatencyMs);
        Assert.NotEqual("OK", sample.Status);
    }

    [Fact]
    public async Task DisabledTarget_EmitsNoSamples()
    {
        using var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("off", UnreachableHost, "#111") { IsEnabled = false });

        var count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(700);
        svc.Stop();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddingTargetMidPump_StartsEmitting()
    {
        using var svc = CreateFast();
        svc.Start();

        var seen = false;
        svc.SampleReceived += _ => seen = true;

        await Task.Delay(200);
        svc.AddOrUpdate(new PingTarget("late", UnreachableHost, "#111"));
        await Task.Delay(1500);
        svc.Stop();

        Assert.True(seen, "Expected at least one sample after late Add");
    }

    [Fact]
    public async Task RemovingTargetMidPump_StopsEmitting()
    {
        using var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("x", UnreachableHost, "#111"));

        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(700);
        svc.Remove(UnreachableHost);
        var afterRemove = Interlocked.Read(ref count);
        await Task.Delay(1500);
        svc.Stop();

        var finalCount = Interlocked.Read(ref count);
        // Allow exactly one extra in-flight sample that was already dispatched.
        Assert.True(finalCount - afterRemove <= 1,
            $"Samples kept arriving after Remove: before={afterRemove}, after={finalCount}");
    }

    [Fact]
    public async Task DisposingWhileRunning_Cleanly_Stops()
    {
        var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("x", UnreachableHost, "#111"));
        svc.Start();
        await Task.Delay(200);

        var ex = Record.Exception(() => svc.Dispose());
        Assert.Null(ex);
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task RapidStartStop_DoesNotLeakOrThrow()
    {
        for (int i = 0; i < 5; i++)
        {
            using var svc = CreateFast();
            svc.AddOrUpdate(new PingTarget("x", UnreachableHost, "#111"));
            svc.Start();
            await Task.Delay(30);
            svc.Stop();
        }
    }

    [Fact]
    public async Task IntervalChange_IsRespectedOnNextTick()
    {
        using var svc = CreateFast();
        svc.AddOrUpdate(new PingTarget("x", UnreachableHost, "#111"));

        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);

        // Fast phase: 100ms interval for 1.2s → expect 8-12 ticks (loose bound)
        svc.Interval = TimeSpan.FromMilliseconds(100);
        svc.Start();
        await Task.Delay(1200);
        var fastCount = Interlocked.Read(ref count);

        // Slow phase: 1s interval for 2.5s → expect ~2 ticks.
        svc.Interval = TimeSpan.FromSeconds(1);
        var startSlow = Interlocked.Read(ref count);
        await Task.Delay(2500);
        var slowDelta = Interlocked.Read(ref count) - startSlow;
        svc.Stop();

        Assert.True(fastCount >= 4,
            $"Fast phase (100ms interval / 1.2s) should produce plenty of samples, got {fastCount}");
        // Slow phase rate must be meaningfully lower than fast phase rate.
        var fastRate = fastCount / 1.2;  // samples per second
        var slowRate = slowDelta / 2.5;
        Assert.True(slowRate < fastRate,
            $"Slow rate ({slowRate:F1}/s) should be lower than fast rate ({fastRate:F1}/s)");
    }
}
