// SysManager · TracerouteMonitorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class TracerouteMonitorServiceTests
{
    private const string Unreachable = "192.0.2.1";

    [Fact]
    public void NotRunning_Initially()
    {
        using var svc = new TracerouteMonitorService();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void AddOrUpdate_StoresByHost()
    {
        using var svc = new TracerouteMonitorService();
        svc.AddOrUpdate(new PingTarget("A", Unreachable, "#111"));
        Assert.True(svc.Targets.ContainsKey(Unreachable));
    }

    [Fact]
    public void AddOrUpdate_OverwritesByHost()
    {
        using var svc = new TracerouteMonitorService();
        svc.AddOrUpdate(new PingTarget("A", Unreachable, "#111"));
        svc.AddOrUpdate(new PingTarget("B", Unreachable, "#222"));
        Assert.Equal("B", svc.Targets[Unreachable].Name);
    }

    [Fact]
    public void Remove_DropsTarget()
    {
        using var svc = new TracerouteMonitorService();
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
        svc.Remove(Unreachable);
        Assert.False(svc.Targets.ContainsKey(Unreachable));
    }

    [Fact]
    public void Remove_UnknownHost_DoesNotThrow()
    {
        using var svc = new TracerouteMonitorService();
        var ex = Record.Exception(() => svc.Remove("nope"));
        Assert.Null(ex);
    }

    [Fact]
    public void Start_FlipsIsRunning()
    {
        using var svc = new TracerouteMonitorService { Interval = TimeSpan.FromMilliseconds(100) };
        svc.Start();
        Assert.True(svc.IsRunning);
        svc.Stop();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        using var svc = new TracerouteMonitorService { Interval = TimeSpan.FromMilliseconds(100) };
        svc.Start();
        svc.Start();
        Assert.True(svc.IsRunning);
        svc.Stop();
    }

    [Fact]
    public void Stop_WhenNotRunning_IsSafe()
    {
        using var svc = new TracerouteMonitorService();
        var ex = Record.Exception(() => svc.Stop());
        Assert.Null(ex);
    }

    [Fact]
    public void IntervalDefault_IsOneMinute()
    {
        var svc = new TracerouteMonitorService();
        Assert.Equal(TimeSpan.FromSeconds(60), svc.Interval);
    }

    [Fact]
    public void IntervalChanges_AreAccepted()
    {
        using var svc = new TracerouteMonitorService();
        svc.Interval = TimeSpan.FromMinutes(5);
        Assert.Equal(TimeSpan.FromMinutes(5), svc.Interval);
    }

    [Fact]
    public void Dispose_StopsAndCleans()
    {
        var svc = new TracerouteMonitorService { Interval = TimeSpan.FromMilliseconds(100) };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
        svc.Start();
        svc.Dispose();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void DisabledTarget_IsSkipped()
    {
        using var svc = new TracerouteMonitorService();
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111") { IsEnabled = false });
        var active = svc.Targets.Values.Where(t => t.IsEnabled).ToList();
        Assert.Empty(active);
    }
}
