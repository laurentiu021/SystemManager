// SysManager · DiskHealthReport edge-case tests
using SysManager.Models;

namespace SysManager.Tests;

public class DiskHealthReportEdgeCaseTests
{
    [Fact]
    public void HealthPercent_NoSmartData_UnknownStatus_ReturnsNull()
    {
        var report = new DiskHealthReport { HealthStatus = "SomethingUnknown" };
        Assert.Null(report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NoSmartData_HealthyStatus_Returns100()
    {
        var report = new DiskHealthReport { HealthStatus = "Healthy" };
        Assert.Equal(100, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NoSmartData_WarningStatus_Returns60()
    {
        var report = new DiskHealthReport { HealthStatus = "Warning" };
        Assert.Equal(60, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NoSmartData_UnhealthyStatus_Returns20()
    {
        var report = new DiskHealthReport { HealthStatus = "Unhealthy" };
        Assert.Equal(20, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_MaxWear_Returns0()
    {
        var report = new DiskHealthReport { WearPercent = 100 };
        Assert.Equal(0, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_WearExceeds100_ClampedTo0()
    {
        var report = new DiskHealthReport { WearPercent = 150 };
        Assert.Equal(0, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NegativeWear_TreatedAsZero()
    {
        var report = new DiskHealthReport { WearPercent = -10 };
        Assert.Equal(100, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_HighTemp_Penalty30()
    {
        var report = new DiskHealthReport { TemperatureC = 75 };
        Assert.Equal(70, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_MedHighTemp_Penalty15()
    {
        var report = new DiskHealthReport { TemperatureC = 65 };
        Assert.Equal(85, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_MedTemp_Penalty5()
    {
        var report = new DiskHealthReport { TemperatureC = 55 };
        Assert.Equal(95, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_LowTemp_NoPenalty()
    {
        var report = new DiskHealthReport { TemperatureC = 40 };
        Assert.Equal(100, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_ReadErrors_CappedAt20Penalty()
    {
        var report = new DiskHealthReport { ReadErrors = 100 };
        Assert.Equal(80, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_WriteErrors_CappedAt20Penalty()
    {
        var report = new DiskHealthReport { WriteErrors = 100 };
        Assert.Equal(80, report.HealthPercent);
    }

    [Fact]
    public void HealthPercent_AllBad_ClampedToZero()
    {
        var report = new DiskHealthReport
        {
            WearPercent = 90,
            TemperatureC = 80,
            ReadErrors = 10,
            WriteErrors = 10
        };
        // 100 - 90 (wear) - 30 (temp) - 20 (read cap) - 20 (write cap) = -60, clamped to 0
        Assert.Equal(0, report.HealthPercent);
    }

    [Fact]
    public void HealthPercentColorHex_HighHealth_Green()
    {
        var report = new DiskHealthReport { HealthStatus = "Healthy" };
        Assert.Equal("#22C55E", report.HealthPercentColorHex);
    }

    [Fact]
    public void HealthPercentColorHex_NullHealth_Red()
    {
        var report = new DiskHealthReport { HealthStatus = "SomethingUnknown" };
        Assert.Equal("#EF4444", report.HealthPercentColorHex);
    }

    [Fact]
    public void TemperatureColorHex_NullTemp_FallsToDefault()
    {
        var report = new DiskHealthReport { TemperatureC = null };
        // null <= 40 returns false in C#, so it falls to _ => "#EF4444"
        Assert.Equal("#EF4444", report.TemperatureColorHex);
    }

    [Fact]
    public void TemperatureGauge_NullTemp_ReturnsZero()
    {
        var report = new DiskHealthReport { TemperatureC = null };
        Assert.Equal(0, report.TemperatureGauge);
    }

    [Fact]
    public void TemperatureGauge_80Degrees_Returns100()
    {
        var report = new DiskHealthReport { TemperatureC = 80 };
        Assert.Equal(100, report.TemperatureGauge);
    }

    [Fact]
    public void TemperatureGauge_40Degrees_Returns50()
    {
        var report = new DiskHealthReport { TemperatureC = 40 };
        Assert.Equal(50, report.TemperatureGauge);
    }

    [Fact]
    public void TemperatureGauge_Over80_ClampedTo100()
    {
        var report = new DiskHealthReport { TemperatureC = 120 };
        Assert.Equal(100, report.TemperatureGauge);
    }

    [Fact]
    public void WearGauge_NullWear_Returns100()
    {
        var report = new DiskHealthReport { WearPercent = null };
        Assert.Equal(100, report.WearGauge);
    }

    [Fact]
    public void WearGauge_ZeroWear_Returns100()
    {
        var report = new DiskHealthReport { WearPercent = 0 };
        Assert.Equal(100, report.WearGauge);
    }

    [Fact]
    public void WearGauge_100Wear_Returns0()
    {
        var report = new DiskHealthReport { WearPercent = 100 };
        Assert.Equal(0, report.WearGauge);
    }

    [Fact]
    public void WearColorHex_NullWear_Grey()
    {
        var report = new DiskHealthReport { WearPercent = null };
        Assert.Equal("#9AA0A6", report.WearColorHex);
    }

    [Fact]
    public void WearColorHex_LowWear_Green()
    {
        var report = new DiskHealthReport { WearPercent = 10 };
        Assert.Equal("#22C55E", report.WearColorHex);
    }

    [Fact]
    public void WearColorHex_HighWear_Red()
    {
        var report = new DiskHealthReport { WearPercent = 95 };
        Assert.Equal("#EF4444", report.WearColorHex);
    }

    [Fact]
    public void PowerOnDisplay_Null_ShowsDash()
    {
        var report = new DiskHealthReport { PowerOnHours = null };
        Assert.Equal("—", report.PowerOnDisplay);
    }

    [Fact]
    public void PowerOnDisplay_Hours_ShowsHours()
    {
        var report = new DiskHealthReport { PowerOnHours = 10 };
        Assert.Equal("10h", report.PowerOnDisplay);
    }

    [Fact]
    public void PowerOnDisplay_Days_ShowsDaysAndHours()
    {
        var report = new DiskHealthReport { PowerOnHours = 50 };
        Assert.Equal("2d 2h", report.PowerOnDisplay);
    }

    [Fact]
    public void PowerOnDisplay_Years_ShowsYears()
    {
        var report = new DiskHealthReport { PowerOnHours = 17520 };
        Assert.Equal("2.0y", report.PowerOnDisplay);
    }

    [Fact]
    public void PropertyChanged_FiredOnWearPercentChange()
    {
        var report = new DiskHealthReport();
        var changed = new List<string>();
        report.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        report.WearPercent = 50;

        Assert.Contains("WearPercent", changed);
    }
}
