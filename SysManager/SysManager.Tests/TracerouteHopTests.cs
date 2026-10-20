// SysManager · TracerouteHopTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
            LatencyMs = 3.5,
            Status = "TtlExpired"
        };
        Assert.Equal(5, h.HopNumber);
        Assert.Equal("192.168.1.1", h.Address);
        Assert.Equal(3.5, h.LatencyMs);
        Assert.Equal("TtlExpired", h.Status);
    }

    [Fact]
    public void PropertyChanged_FiresOnLatencyChange()
    {
        var h = new TracerouteHop();
        var changed = h.RecordPropertyChanges();
        h.LatencyMs = 10.0;
        Assert.Contains("LatencyMs", changed);
    }

    [Fact]
    public void AHop_CarriesNoHostName_SoNoTraceWaitsToLookOneUp()
    {
        // Each hop used to wait for a reverse lookup whose answer nothing displayed. Showing names would mean adding
        // a column for them, and the lookup with it, on purpose.
        Assert.Null(typeof(TracerouteHop).GetProperty("HostName"));
        var source = File.ReadAllText(Path.Combine(TestPaths.AppProject(), "Services", "TracerouteService.cs"));
        Assert.Contains("GetHostAddressesAsync", source, StringComparison.Ordinal);   // the source that was read
        Assert.DoesNotContain("GetHostEntryAsync", source, StringComparison.Ordinal);
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
