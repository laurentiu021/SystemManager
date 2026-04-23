// SysManager · BatteryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BatteryService"/>. Pure-logic tests only —
/// WMI calls are not exercised in CI (no battery on build agents).
/// </summary>
public class BatteryServiceTests
{
    // ── MapBatteryStatus ──

    [Theory]
    [InlineData((ushort)1, "Discharging")]
    [InlineData((ushort)2, "AC power")]
    [InlineData((ushort)3, "Fully charged")]
    [InlineData((ushort)4, "Low")]
    [InlineData((ushort)5, "Critical")]
    [InlineData((ushort)6, "Charging")]
    [InlineData((ushort)7, "Charging (high)")]
    [InlineData((ushort)8, "Charging (low)")]
    [InlineData((ushort)9, "Charging (critical)")]
    [InlineData((ushort)10, "Undefined")]
    [InlineData((ushort)11, "Partially charged")]
    [InlineData((ushort)0, "Unknown")]
    [InlineData((ushort)99, "Unknown")]
    public void MapBatteryStatus_ReturnsExpected(ushort code, string expected)
    {
        Assert.Equal(expected, BatteryService.MapBatteryStatus(code));
    }

    // ── MapChemistry ──

    [Theory]
    [InlineData((ushort)1, "Other")]
    [InlineData((ushort)2, "Unknown")]
    [InlineData((ushort)3, "Lead Acid")]
    [InlineData((ushort)4, "Nickel Cadmium")]
    [InlineData((ushort)5, "Nickel Metal Hydride")]
    [InlineData((ushort)6, "Lithium-ion")]
    [InlineData((ushort)7, "Zinc Air")]
    [InlineData((ushort)8, "Lithium Polymer")]
    [InlineData((ushort)0, "Unknown")]
    [InlineData((ushort)99, "Unknown")]
    public void MapChemistry_ReturnsExpected(ushort code, string expected)
    {
        Assert.Equal(expected, BatteryService.MapChemistry(code));
    }

    // ── BatteryInfo model ──

    [Fact]
    public void BatteryInfo_HealthPercent_CalculatesCorrectly()
    {
        var info = new BatteryInfo
        {
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 45000
        };
        Assert.Equal(90.0, info.HealthPercent);
    }

    [Fact]
    public void BatteryInfo_HealthPercent_ZeroDesign_ReturnsZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0, FullChargeCapacityMWh = 1000 };
        Assert.Equal(0, info.HealthPercent);
    }

    [Fact]
    public void BatteryInfo_WearPercent_CalculatesCorrectly()
    {
        var info = new BatteryInfo
        {
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 45000
        };
        Assert.Equal(10.0, info.WearPercent);
    }

    [Fact]
    public void BatteryInfo_WearPercent_ZeroDesign_ReturnsZero()
    {
        var info = new BatteryInfo { DesignCapacityMWh = 0 };
        Assert.Equal(0, info.WearPercent);
    }

    [Fact]
    public void BatteryInfo_RuntimeDisplay_PluggedIn()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = -1 };
        Assert.Equal("Plugged in", info.RuntimeDisplay);
    }

    [Fact]
    public void BatteryInfo_RuntimeDisplay_Calculating()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 0 };
        Assert.Equal("Calculating…", info.RuntimeDisplay);
    }

    [Fact]
    public void BatteryInfo_RuntimeDisplay_FormatsHoursMinutes()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 150 };
        Assert.Equal("2h 30m", info.RuntimeDisplay);
    }

    [Fact]
    public void BatteryInfo_RuntimeDisplay_LessThanOneHour()
    {
        var info = new BatteryInfo { EstimatedRuntimeMinutes = 45 };
        Assert.Equal("0h 45m", info.RuntimeDisplay);
    }

    [Fact]
    public void BatteryInfo_DefaultValues()
    {
        var info = new BatteryInfo();
        Assert.False(info.HasBattery);
        Assert.Equal("", info.Name);
        Assert.Equal("", info.Status);
        Assert.Equal(0, info.ChargePercent);
        Assert.Equal(0u, info.DesignCapacityMWh);
        Assert.Equal(0u, info.FullChargeCapacityMWh);
        Assert.Equal(0, info.CycleCount);
        Assert.Equal(0, info.EstimatedRuntimeMinutes);
        Assert.Equal("", info.Chemistry);
        Assert.Equal("", info.Manufacturer);
    }

    [Fact]
    public void BatteryInfo_PropertyChange_Notifies()
    {
        var info = new BatteryInfo();
        var changed = new List<string>();
        info.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        info.HasBattery = true;
        info.Name = "Test Battery";
        info.ChargePercent = 85;
        info.Status = "Charging";
        info.CycleCount = 200;

        Assert.Contains("HasBattery", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("ChargePercent", changed);
        Assert.Contains("Status", changed);
        Assert.Contains("CycleCount", changed);
    }

    // ── GetBatteryInfo (safe to call — returns "no battery" on desktops) ──

    [Fact]
    public void GetBatteryInfo_DoesNotThrow()
    {
        var info = BatteryService.GetBatteryInfo();
        Assert.NotNull(info);
        // On CI/desktop: HasBattery will be false, Status = "No battery detected"
        if (!info.HasBattery)
            Assert.Equal("No battery detected", info.Status);
    }

    [Fact]
    public async Task GetBatteryInfoAsync_DoesNotThrow()
    {
        var service = new BatteryService();
        var info = await service.GetBatteryInfoAsync();
        Assert.NotNull(info);
    }
}
