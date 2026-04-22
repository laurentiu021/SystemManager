// SysManager · PingMonitorEdgeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class PingMonitorEdgeTests
{
    private const string Unreachable = "192.0.2.1";

    [Fact]
    public void TimeoutMsDefault_IsTwoSeconds()
    {
        var svc = new PingMonitorService();
        Assert.Equal(2000, svc.TimeoutMs);
    }

    [Fact]
    public void IntervalDefault_IsOneSecond()
    {
        var svc = new PingMonitorService();
        Assert.Equal(TimeSpan.FromSeconds(1), svc.Interval);
    }

    [Fact]
    public void TimeoutMs_Changes_AreRespected()
    {
        var svc = new PingMonitorService { TimeoutMs = 500 };
        Assert.Equal(500, svc.TimeoutMs);
    }

    [Fact]
    public void Targets_InitiallyEmpty()
    {
        var svc = new PingMonitorService();
        Assert.Empty(svc.Targets);
    }

    [Fact]
    public async Task AddTarget_WithEmptyHost_NoSamples()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 300
        };
        svc.AddOrUpdate(new PingTarget("empty", "", "#111"));
        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(400);
        svc.Stop();
        Assert.Equal(0, Interlocked.Read(ref count));
    }

    [Fact]
    public async Task AddTarget_WithWhitespaceHost_NoSamples()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 300
        };
        svc.AddOrUpdate(new PingTarget("ws", "   ", "#111"));
        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(400);
        svc.Stop();
        Assert.Equal(0, Interlocked.Read(ref count));
    }

    [Fact]
    public async Task ZeroTargets_ButRunning_DoesNotThrow()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 300
        };
        var ex = await Record.ExceptionAsync(async () =>
        {
            svc.Start();
            await Task.Delay(300);
            svc.Stop();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task ReenableTarget_ResumesSamples()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        var t = new PingTarget("x", Unreachable, "#111") { IsEnabled = false };
        svc.AddOrUpdate(t);
        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(400);
        Assert.Equal(0, Interlocked.Read(ref count));
        t.IsEnabled = true;
        await Task.Delay(700);
        svc.Stop();
        Assert.True(Interlocked.Read(ref count) > 0);
    }

    [Fact]
    public async Task AllSamples_HaveCorrectHost()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        svc.AddOrUpdate(new PingTarget("a", Unreachable, "#111"));
        var seen = new List<string>();
        var gate = new object();
        svc.SampleReceived += s =>
        {
            lock (gate) seen.Add(s.Host);
        };
        svc.Start();
        await Task.Delay(500);
        svc.Stop();
        Assert.All(seen, h => Assert.Equal(Unreachable, h));
    }

    [Fact]
    public async Task Samples_HaveRecentTimestamps()
    {
        // Since pings are fire-and-forget, completion order is not guaranteed,
        // but every sample's timestamp must be within the monitoring window.
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        svc.AddOrUpdate(new PingTarget("a", Unreachable, "#111"));
        var timestamps = new List<DateTime>();
        var gate = new object();
        svc.SampleReceived += s =>
        {
            lock (gate) timestamps.Add(s.Timestamp);
        };
        var before = DateTime.UtcNow.AddSeconds(-1);
        svc.Start();
        await Task.Delay(700);
        svc.Stop();
        var after = DateTime.UtcNow.AddSeconds(1);

        foreach (var ts in timestamps)
            Assert.InRange(ts, before, after);
    }
}
