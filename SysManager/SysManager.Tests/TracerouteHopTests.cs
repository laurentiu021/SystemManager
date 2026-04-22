using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TracerouteHop"/> — observable model for traceroute results.
/// </summary>
public class TracerouteHopTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var h = new TracerouteHop();
        Assert.Equal(0, h.HopNumber);
        Assert.Equal("*", h.Address);
        Assert.Equal("", h.HostName);
        Assert.Null(h.LatencyMs);
        Assert.Equal("", h.Status);
    }

    [Fact]
    public void AllProperties_Settable()
    {
        var h = new TracerouteHop
        {
            HopNumber = 5,
            Address = "192.168.1.1",
            HostName = "router.local",
            LatencyMs = 3.5,
            Status = "TtlExpired"
        };
        Assert.Equal(5, h.HopNumber);
        Assert.Equal("192.168.1.1", h.Address);
        Assert.Equal("router.local", h.HostName);
        Assert.Equal(3.5, h.LatencyMs);
        Assert.Equal("TtlExpired", h.Status);
    }

    [Fact]
    public void PropertyChanged_FiresOnLatencyChange()
    {
        var h = new TracerouteHop();
        var changed = new List<string>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        h.LatencyMs = 10.0;
        Assert.Contains("LatencyMs", changed);
    }

    [Fact]
    public void PropertyChanged_FiresOnHostNameChange()
    {
        var h = new TracerouteHop();
        var changed = new List<string>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        h.HostName = "resolved.host.com";
        Assert.Contains("HostName", changed);
    }

    [Fact]
    public void TimeoutHop_HasNullLatency()
    {
        var h = new TracerouteHop
        {
            HopNumber = 3,
            Address = "*",
            Status = "Timeout"
        };
        Assert.Null(h.LatencyMs);
        Assert.Equal("*", h.Address);
    }
}
