// SysManager · BatteryInfo computed property edge-case tests
using SysManager.Models;

namespace SysManager.Tests;

public class BatteryInfoEdgeCaseTests
{
    [Fact]
    public void HealthPercent_ZeroDesignCapacity_ReturnsZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0, FullChargeCapacityMWh = 5000 };
        Assert.Equal(0, info.HealthPercent);
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
    public void WearPercent_ZeroDesignCapacity_ReturnsZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0, FullChargeCapacityMWh = 5000 };
        Assert.Equal(0, info.WearPercent);
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
}
