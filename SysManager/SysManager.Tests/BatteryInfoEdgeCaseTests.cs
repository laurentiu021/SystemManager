// SysManager · BatteryInfoEdgeCaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using SysManager.Models;

namespace SysManager.Tests;

public class BatteryInfoEdgeCaseTests
{
    [Fact]
    public void HealthPercent_ZeroDesignCapacity_ReturnsUnavailable()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0, FullChargeCapacityMWh = 5000 };
        Assert.Equal(-1, info.HealthPercent);
    }

    [Fact]
    public void HealthPercent_EqualCapacities_Returns100()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 50000 };
        Assert.Equal(100.0, info.HealthPercent);
    }

    [Fact]
    public void HealthPercent_HalfWorn_Returns50()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 25000 };
        Assert.Equal(50.0, info.HealthPercent);
    }

    [Fact]
    public void HealthPercent_NewBatteryOverDesign_ClampedTo100()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 40000, FullChargeCapacityMWh = 44000 };
        // QA-005 fix: clamped to 100 max
        Assert.Equal(100, info.HealthPercent);
    }

    [Fact]
    public void WearPercent_ZeroDesignCapacity_ReturnsUnavailable()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0, FullChargeCapacityMWh = 5000 };
        Assert.Equal(-1, info.WearPercent);
    }

    [Fact]
    public void WearPercent_EqualCapacities_ReturnsZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 50000 };
        Assert.Equal(0, info.WearPercent);
    }

    [Fact]
    public void WearPercent_HalfWorn_Returns50()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 25000 };
        Assert.Equal(50.0, info.WearPercent);
    }

    [Fact]
    public void WearPercent_NewBatteryOverDesign_ClampedToZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 40000, FullChargeCapacityMWh = 44000 };
        // QA-005 fix: clamped to 0 min
        Assert.Equal(0, info.WearPercent);
    }

    [Fact]
    public void RuntimeDisplay_PluggedIn_ShowsPluggedIn()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = -1 };
        Assert.Equal("Plugged in", info.RuntimeDisplay);
    }

    [Fact]
    public void RuntimeDisplay_Calculating_ShowsCalculating()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 0 };
        Assert.Contains("Calculating", info.RuntimeDisplay);
    }

    [Fact]
    public void RuntimeDisplay_90Minutes_ShowsFormatted()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 90 };
        Assert.Equal("1h 30m", info.RuntimeDisplay);
    }

    [Fact]
    public void RuntimeDisplay_60Minutes_ShowsFormatted()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 60 };
        Assert.Equal("1h 0m", info.RuntimeDisplay);
    }

    [Fact]
    public void RuntimeDisplay_30Minutes_ShowsFormatted()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 30 };
        Assert.Equal("0h 30m", info.RuntimeDisplay);
    }

    [Fact]
    public void PropertyChanged_FiredOnChargePercentChange()
    {
        var info = new BatteryInfo();
        var changes = new List<string>();
        info.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

        info.ChargePercent = 75;

        Assert.Contains("ChargePercent", changes);
    }

    [Fact]
    public void DefaultValues_AllStringsEmpty()
    {
        var info = new BatteryInfo();
        Assert.Equal("", info.Name);
        Assert.Equal("", info.Status);
        Assert.Equal("", info.Chemistry);
        Assert.Equal("", info.Manufacturer);
    }

    // ── Display formatting ──
    //
    // HealthPercent/WearPercent return -1 to mean "capacity could not be read" (the root\WMI
    // query needs elevation). The view bound those numbers directly and appended "%", so an
    // unelevated run showed "-1%" as though it were a measurement. These pin the sentinel
    // never reaching the screen as a number.

    [Theory]
    [InlineData(0u, 5000u)]      // design capacity unreadable
    [InlineData(50000u, 0u)]     // full-charge capacity unreadable
    [InlineData(0u, 0u)]         // neither readable
    public void Display_WhenCapacityUnavailable_SaysNotAvailable(uint design, uint full)
    {
        var info = new BatteryInfo { DesignCapacityMWh = design, FullChargeCapacityMWh = full };

        Assert.False(info.HasCapacityData);
        Assert.Equal("Not available", info.HealthDisplay);
        Assert.Equal("Not available", info.WearDisplay);
        Assert.DoesNotContain("-1", info.HealthDisplay);
        Assert.DoesNotContain("-1", info.WearDisplay);
    }

    [Fact]
    public void Display_WhenCapacityAvailable_ShowsPercentages()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 40000 };

        Assert.True(info.HasCapacityData);
        Assert.Equal("80%", info.HealthDisplay);
        Assert.Equal("20%", info.WearDisplay);
    }

    [Fact]
    public void Display_HealthyBattery_ShowsZeroWearNotNotAvailable()
    {
        // 100% health means 0% wear — a real figure that must not be confused with missing
        // data just because the number is zero.
        var info = new BatteryInfo { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 50000 };

        Assert.True(info.HasCapacityData);
        Assert.Equal("100%", info.HealthDisplay);
        Assert.Equal("0%", info.WearDisplay);
    }

    [Fact]
    public void Display_TracksCapacityArrivingLater()
    {
        // The view binds the Display properties, so they must re-read when capacity arrives
        // rather than staying stuck at "Not available" after an elevated refresh succeeds.
        var info = new BatteryInfo();
        Assert.Equal("Not available", info.HealthDisplay);

        info.DesignCapacityMWh = 50000;
        info.FullChargeCapacityMWh = 45000;

        Assert.Equal("90%", info.HealthDisplay);
        Assert.Equal("10%", info.WearDisplay);
    }

    [Fact]
    public void Display_RaisesPropertyChanged_WhenCapacityArrives()
    {
        var info = new BatteryInfo();
        var changed = new List<string>();
        info.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        info.FullChargeCapacityMWh = 45000;

        Assert.Contains(nameof(BatteryInfo.HealthDisplay), changed);
        Assert.Contains(nameof(BatteryInfo.WearDisplay), changed);
        Assert.Contains(nameof(BatteryInfo.HasCapacityData), changed);
    }
}
