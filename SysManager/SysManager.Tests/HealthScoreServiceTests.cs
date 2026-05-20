// SysManager · HealthScoreServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="HealthScoreService"/> — validates scoring logic
/// for each component and the aggregation formula.
/// </summary>
public class HealthScoreServiceTests
{
    // ---------- construction ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = new HealthScoreService(
            new SystemInfoService(), new DiskHealthService(), new BatteryService());
        Assert.NotNull(svc);
    }

    // ---------- ComputeDiskScore ----------

    [Fact]
    public void ComputeDiskScore_NullDisks_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeDiskScore(null));
    }

    [Fact]
    public void ComputeDiskScore_EmptyList_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeDiskScore([]));
    }

    [Fact]
    public void ComputeDiskScore_AllHealthy_Returns100()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Healthy" },
            new() { HealthStatus = "Healthy" }
        };
        Assert.Equal(100, HealthScoreService.ComputeDiskScore(disks));
    }

    [Fact]
    public void ComputeDiskScore_OneWarning_Returns60()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Healthy" },
            new() { HealthStatus = "Warning" }
        };
        Assert.Equal(60, HealthScoreService.ComputeDiskScore(disks));
    }

    [Fact]
    public void ComputeDiskScore_OneUnhealthy_Returns20()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Unhealthy" }
        };
        Assert.Equal(20, HealthScoreService.ComputeDiskScore(disks));
    }

    // ---------- ComputeRamScore ----------

    [Fact]
    public void ComputeRamScore_NullSnapshot_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeRamScore(null));
    }

    [Fact]
    public void ComputeRamScore_LowUsage_Returns100()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 40);
        Assert.Equal(100, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_60Percent_Returns100()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 60);
        Assert.Equal(100, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_75Percent_Returns75()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 75);
        Assert.Equal(75, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_90Percent_Returns40()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 90);
        Assert.Equal(40, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_98Percent_Returns10()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 98);
        Assert.Equal(10, HealthScoreService.ComputeRamScore(snapshot));
    }

    // ---------- ComputeUptimeScore ----------

    [Fact]
    public void ComputeUptimeScore_NullSnapshot_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeUptimeScore(null));
    }

    [Fact]
    public void ComputeUptimeScore_1Day_Returns100()
    {
        var snapshot = MakeSnapshot(uptimeDays: 1);
        Assert.Equal(100, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_5Days_Returns90()
    {
        var snapshot = MakeSnapshot(uptimeDays: 5);
        Assert.Equal(90, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_10Days_Returns70()
    {
        var snapshot = MakeSnapshot(uptimeDays: 10);
        Assert.Equal(70, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_20Days_Returns50()
    {
        var snapshot = MakeSnapshot(uptimeDays: 20);
        Assert.Equal(50, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_45Days_Returns15()
    {
        var snapshot = MakeSnapshot(uptimeDays: 45);
        Assert.Equal(15, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    // ---------- ComputeBatteryScore ----------

    [Fact]
    public void ComputeBatteryScore_NullBattery_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(null));
    }

    [Fact]
    public void ComputeBatteryScore_NoBattery_Returns100()
    {
        var battery = new BatteryInfo { HasBattery = false };
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_HealthyBattery_Returns100()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 48000 // 96% health
        };
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_DegradedBattery_Returns80()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 35000 // 70% health
        };
        Assert.Equal(80, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_WornBattery_Returns55()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 22000 // 44% health
        };
        Assert.Equal(55, HealthScoreService.ComputeBatteryScore(battery));
    }

    // ---------- HealthScoreResult model ----------

    [Theory]
    [InlineData(100, "#22C55E")]
    [InlineData(80, "#22C55E")]
    [InlineData(79, "#F59E0B")]
    [InlineData(50, "#F59E0B")]
    [InlineData(49, "#EF4444")]
    [InlineData(0, "#EF4444")]
    public void HealthScoreResult_ColorHex_MatchesScore(int score, string expectedColor)
    {
        var result = new HealthScoreResult { Score = score };
        Assert.Equal(expectedColor, result.ColorHex);
    }

    [Theory]
    [InlineData(95, "Excellent")]
    [InlineData(85, "Good")]
    [InlineData(65, "Fair")]
    [InlineData(45, "Needs attention")]
    [InlineData(20, "Poor")]
    public void HealthScoreResult_Label_MatchesScore(int score, string expectedLabel)
    {
        var result = new HealthScoreResult { Score = score };
        Assert.Equal(expectedLabel, result.Label);
    }

    [Fact]
    public void HealthRecommendation_CriticalSeverity_RedColor()
    {
        var rec = new HealthRecommendation { Message = "Test", Severity = "critical" };
        Assert.Equal("#EF4444", rec.ColorHex);
    }

    [Fact]
    public void HealthRecommendation_WarningSeverity_AmberColor()
    {
        var rec = new HealthRecommendation { Message = "Test", Severity = "warning" };
        Assert.Equal("#F59E0B", rec.ColorHex);
    }

    // ---------- helpers ----------

    private static SystemSnapshot MakeSnapshot(double ramUsedPct = 50, int uptimeDays = 1)
    {
        double totalGB = 16;
        double usedGB = totalGB * ramUsedPct / 100;
        return new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(uptimeDays), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(totalGB, totalGB - usedGB, usedGB, ramUsedPct, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
    }
}
