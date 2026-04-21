using System.ComponentModel;
using SysManager.Models;

namespace SysManager.IntegrationTests;

public class SpeedTestResultTests
{
    [Fact]
    public void StoresAllFields()
    {
        var r = new SpeedTestResult("HTTP", 125.5, 23.1, 14.2, "Cloudflare", DateTime.Now);
        Assert.Equal("HTTP", r.Engine);
        Assert.Equal(125.5, r.DownloadMbps);
        Assert.Equal(23.1, r.UploadMbps);
        Assert.Equal(14.2, r.PingMs);
        Assert.Equal("Cloudflare", r.Server);
    }

    [Fact]
    public void TwoEngines_AreDistinct()
    {
        var http = new SpeedTestResult("HTTP", 100, 10, 20, "cf", DateTime.Now);
        var ookla = new SpeedTestResult("Ookla", 100, 10, 20, "cf", DateTime.Now);
        Assert.NotEqual(http.Engine, ookla.Engine);
    }

    [Fact]
    public void Records_EqualByValue_WhenTimestampsMatch()
    {
        var t = new DateTime(2026, 1, 1);
        var a = new SpeedTestResult("HTTP", 1, 2, 3, "x", t);
        var b = new SpeedTestResult("HTTP", 1, 2, 3, "x", t);
        Assert.Equal(a, b);
    }
}

public class PingSampleTests
{
    [Fact]
    public void StoresAllFields()
    {
        var s = new PingSample(DateTime.UtcNow, "1.1.1.1", 12.5, "OK");
        Assert.Equal("1.1.1.1", s.Host);
        Assert.Equal(12.5, s.LatencyMs);
        Assert.Equal("OK", s.Status);
    }

    [Fact]
    public void LatencyCanBeNull_ForTimeouts()
    {
        var s = new PingSample(DateTime.UtcNow, "x", null, "TimedOut");
        Assert.Null(s.LatencyMs);
    }

    [Fact]
    public void Records_EqualByValue()
    {
        var t = DateTime.UnixEpoch;
        var a = new PingSample(t, "h", 10, "OK");
        var b = new PingSample(t, "h", 10, "OK");
        Assert.Equal(a, b);
    }
}

public class TracerouteHopTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var h = new TracerouteHop();
        Assert.Equal(0, h.HopNumber);
        Assert.Equal("*", h.Address);
        Assert.Null(h.LatencyMs);
        Assert.Equal("", h.Status);
    }

    [Fact]
    public void PropertyChanges_RaiseEvents()
    {
        var h = new TracerouteHop();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)h).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        h.HopNumber = 5;
        h.Address = "1.2.3.4";
        h.LatencyMs = 12;
        h.HostName = "host.example";
        h.Status = "Success";
        Assert.Contains(nameof(TracerouteHop.HopNumber), raised);
        Assert.Contains(nameof(TracerouteHop.Address), raised);
        Assert.Contains(nameof(TracerouteHop.LatencyMs), raised);
        Assert.Contains(nameof(TracerouteHop.HostName), raised);
        Assert.Contains(nameof(TracerouteHop.Status), raised);
    }
}
