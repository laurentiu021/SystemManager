// SysManager · DiskHealthReportTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

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

    // ---------- HealthPercent ----------

    [Fact]
    public void HealthPercent_PerfectDisk_Returns100()
    {
        var r = new DiskHealthReport { WearPercent = 0, TemperatureC = 35, ReadErrors = 0, WriteErrors = 0 };
        Assert.Equal(100, r.HealthPercent);
    }

    [Fact]
    public void HealthPercent_WornDisk_DeductsWear()
    {
        var r = new DiskHealthReport { WearPercent = 40, TemperatureC = 35, ReadErrors = 0, WriteErrors = 0 };
        Assert.Equal(60, r.HealthPercent);
    }

    [Fact]
    public void HealthPercent_HotDisk_DeductsTemperature()
    {
        var r = new DiskHealthReport { WearPercent = 0, TemperatureC = 75, ReadErrors = 0, WriteErrors = 0 };
        Assert.Equal(70, r.HealthPercent);
    }

    [Fact]
    public void HealthPercent_WithErrors_DeductsErrors()
    {
        var r = new DiskHealthReport { WearPercent = 0, TemperatureC = 35, ReadErrors = 2, WriteErrors = 1 };
        Assert.Equal(85, r.HealthPercent); // 100 - 10 (2*5) - 5 (1*5)
    }

    [Fact]
    public void HealthPercent_NoSmartData_FallsBackToHealthStatus()
    {
        Assert.Equal(100, new DiskHealthReport { HealthStatus = "Healthy" }.HealthPercent);
        Assert.Equal(60, new DiskHealthReport { HealthStatus = "Warning" }.HealthPercent);
        Assert.Equal(20, new DiskHealthReport { HealthStatus = "Unhealthy" }.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NoData_ReturnsNull()
    {
        Assert.Null(new DiskHealthReport { HealthStatus = "" }.HealthPercent);
    }

    [Fact]
    public void HealthPercent_ClampsToZero()
    {
        var r = new DiskHealthReport { WearPercent = 100, TemperatureC = 80, ReadErrors = 10, WriteErrors = 10 };
        Assert.Equal(0, r.HealthPercent);
    }

    // ---------- Temperature color ----------

    [Theory]
    [InlineData(30, "#22C55E")]
    [InlineData(45, "#F59E0B")]
    [InlineData(55, "#F87171")]
    [InlineData(65, "#EF4444")]
    public void TemperatureColorHex_ReturnsCorrectColor(double temp, string expected)
    {
        var r = new DiskHealthReport { TemperatureC = temp };
        Assert.Equal(expected, r.TemperatureColorHex);
    }

    // ---------- Gauge properties ----------

    [Fact]
    public void TemperatureGauge_MapsCorrectly()
    {
        Assert.Equal(50, new DiskHealthReport { TemperatureC = 40 }.TemperatureGauge);
        Assert.Equal(0, new DiskHealthReport().TemperatureGauge);
    }

    [Fact]
    public void WearGauge_InvertsWear()
    {
        Assert.Equal(80, new DiskHealthReport { WearPercent = 20 }.WearGauge);
        Assert.Equal(100, new DiskHealthReport().WearGauge);
    }

    // ---------- PowerOnDisplay ----------

    [Theory]
    [InlineData(null, "—")]
    [InlineData(12L, "12h")]
    [InlineData(100L, "4d 4h")]
    [InlineData(10000L, "1.1y")]
    public void PowerOnDisplay_FormatsCorrectly(long? hours, string expected)
    {
        var r = new DiskHealthReport { PowerOnHours = hours };
        Assert.Equal(expected, r.PowerOnDisplay);
    }
}
