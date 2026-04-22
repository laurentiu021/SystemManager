using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskHealthReport"/> — observable model for disk health.
/// </summary>
public class DiskHealthReportTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var r = new DiskHealthReport();
        Assert.Equal("", r.FriendlyName);
        Assert.Equal("", r.MediaType);
        Assert.Equal("", r.BusType);
        Assert.Equal(0, r.SizeGB);
        Assert.Equal("", r.HealthStatus);
        Assert.Null(r.TemperatureC);
        Assert.Null(r.TemperatureMaxC);
        Assert.Null(r.WearPercent);
        Assert.Null(r.PowerOnHours);
        Assert.Null(r.ReadErrors);
        Assert.Null(r.WriteErrors);
        Assert.Null(r.StartStopCount);
        Assert.Equal("", r.Verdict);
        Assert.Equal("#9AA0A6", r.VerdictColorHex);
    }

    [Fact]
    public void PropertyChanged_FiresOnVerdictChange()
    {
        var r = new DiskHealthReport();
        var changed = new List<string>();
        r.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        r.Verdict = "Healthy";
        Assert.Contains("Verdict", changed);
    }

    [Fact]
    public void PropertyChanged_FiresOnColorChange()
    {
        var r = new DiskHealthReport();
        var changed = new List<string>();
        r.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        r.VerdictColorHex = "#22C55E";
        Assert.Contains("VerdictColorHex", changed);
    }

    [Fact]
    public void AllProperties_Settable()
    {
        var r = new DiskHealthReport
        {
            FriendlyName = "Samsung 980 PRO",
            MediaType = "SSD",
            BusType = "NVMe",
            SizeGB = 1000,
            HealthStatus = "Healthy",
            TemperatureC = 38,
            TemperatureMaxC = 70,
            WearPercent = 5,
            PowerOnHours = 12000,
            ReadErrors = 0,
            WriteErrors = 0,
            StartStopCount = 500,
            Verdict = "Healthy — 38 °C · wear 5%",
            VerdictColorHex = "#22C55E"
        };
        Assert.Equal("Samsung 980 PRO", r.FriendlyName);
        Assert.Equal(38.0, r.TemperatureC);
        Assert.Equal(5, r.WearPercent);
        Assert.Equal(12000L, r.PowerOnHours);
    }
}
