// SysManager · DiskHealthReportEdgeCaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using SysManager.Helpers;
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
        Assert.Equal(StatusColors.Good, report.HealthPercentColorHex);
    }

    [Fact]
    public void HealthPercentColorHex_NullHealth_IsNeutralGrey()
    {
        // When no SMART data is available HealthPercent is null; the swatch must show
        // the neutral "unknown" grey, not the failing red — consistent with
        // TemperatureColorHex's null arm below.
        var report = new DiskHealthReport { HealthStatus = "SomethingUnknown" };
        Assert.Null(report.HealthPercent);
        Assert.Equal(StatusColors.Neutral, report.HealthPercentColorHex);
    }

    [Fact]
    public void TemperatureColorHex_NullTemp_FallsToDefault()
    {
        var report = new DiskHealthReport { TemperatureC = null };
        // QA-004 fix: null temperature returns grey (no sensor) instead of red
        Assert.Equal(StatusColors.Neutral, report.TemperatureColorHex);
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
    public void WearGauge_NullWear_IsAnEmptyBar()
    {
        // It was 100: a drive that does not report its wear read "LIFE REMAINING 100%" with a full bar (#2600).
        var report = new DiskHealthReport { WearPercent = null };
        Assert.Equal(0, report.WearGauge);
        Assert.Equal("—", report.WearDisplay);
    }

    [Theory]
    [InlineData(0, "100%")]
    [InlineData(7, "93%")]
    [InlineData(100, "0%")]
    [InlineData(130, "0%")]
    public void WearDisplay_IsTheLifeRemaining(int wear, string expected)
        => Assert.Equal(expected, new DiskHealthReport { WearPercent = wear }.WearDisplay);

    // ── Figures the drive does not report read "—" (#2600) ─────────────────
    //
    // The card bound them through a StringFormat with a FallbackValue of "—", and a fallback is for a binding that fails,
    // not for a value that is null: the health score read a bare "%", the temperature " °C", and the error counts nothing.

    [Fact]
    public void WithNothingReported_EveryFigureReadsADash_AndEveryBarIsEmpty()
    {
        var report = new DiskHealthReport { HealthStatus = "SomethingUnknown" };

        Assert.Equal("—", report.HealthDisplay);
        Assert.Equal(0, report.HealthGauge);
        Assert.Equal("—", report.TemperatureDisplay);
        Assert.Equal("—", report.WearDisplay);
        Assert.Equal(0, report.WearGauge);
        Assert.Equal("—", report.ReadErrorsDisplay);
        Assert.Equal("—", report.WriteErrorsDisplay);
    }

    [Fact]
    public void ReportedFigures_ReadAsTheyDidBefore()
    {
        var report = new DiskHealthReport
        {
            HealthStatus = "Healthy",
            TemperatureC = 41.6,
            WearPercent = 12,
            ReadErrors = 0,
            WriteErrors = 3,
        };

        Assert.Equal($"{report.HealthPercent}%", report.HealthDisplay);
        Assert.Equal(report.HealthPercent, report.HealthGauge);
        Assert.Equal("42 °C", report.TemperatureDisplay);
        Assert.Equal("88%", report.WearDisplay);
        Assert.Equal("0", report.ReadErrorsDisplay);
        Assert.Equal("3", report.WriteErrorsDisplay);
    }

    [Fact]
    public void ChangingAFigure_RaisesItsDisplay()
    {
        var report = new DiskHealthReport();
        var raised = report.RecordPropertyChanges();

        report.WearPercent = 5;
        report.TemperatureC = 30;
        report.ReadErrors = 1;
        report.WriteErrors = 1;
        report.HealthStatus = "Healthy";

        foreach (var name in new[] { nameof(DiskHealthReport.WearDisplay), nameof(DiskHealthReport.TemperatureDisplay),
                                     nameof(DiskHealthReport.ReadErrorsDisplay), nameof(DiskHealthReport.WriteErrorsDisplay),
                                     nameof(DiskHealthReport.HealthDisplay), nameof(DiskHealthReport.HealthGauge) })
            Assert.Contains(name, raised);
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
        Assert.Equal(StatusColors.Neutral, report.WearColorHex);
    }

    [Fact]
    public void WearColorHex_LowWear_Green()
    {
        var report = new DiskHealthReport { WearPercent = 10 };
        Assert.Equal(StatusColors.Good, report.WearColorHex);
    }

    [Fact]
    public void WearColorHex_HighWear_Red()
    {
        var report = new DiskHealthReport { WearPercent = 95 };
        Assert.Equal(StatusColors.Bad, report.WearColorHex);
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
        var changed = report.RecordPropertyChanges();

        report.WearPercent = 50;

        Assert.Contains("WearPercent", changed);
    }
}
