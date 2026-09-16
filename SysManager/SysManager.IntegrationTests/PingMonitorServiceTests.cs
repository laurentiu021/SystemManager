// SysManager · PingMonitorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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

    // The interval-change test that used to live here counted how many real ticks fit in 1.2 real
    // seconds, so it measured the host's spare CPU rather than the service, and went red once during
    // a full local run (2 samples against a floor of 4). The pump's delay now comes from an injected
    // TimeProvider, so the cadence is asserted exactly and without sleeping in
    // SysManager.Tests.PingMonitorServiceLifecycleTests. What stays here is what genuinely needs the
    // real network stack: that samples arrive at all, and that add/remove mid-pump is honoured.
}
