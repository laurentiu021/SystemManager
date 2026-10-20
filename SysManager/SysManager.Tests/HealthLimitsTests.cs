// SysManager · HealthLimitsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The Quick Tune-Up card and the Health Score card give the same advice about restarting and about memory (#2877).
/// </summary>
/// <remarks>
/// The Tune-Up card warned at 14 days and 85%, and the Health Score card below it recommended a restart past 7 days and
/// closing apps past 80%, so a PC up for 10 days was told both that it had restarted recently and that it should.
/// Asserted across both sides of each limit, so neither card can move its edge alone.
/// </remarks>
public class HealthLimitsTests
{
    private static SystemSnapshot Snapshot(double ramUsedPercent = 50, double uptimeDays = 1)
    {
        const double TotalGB = 16;
        var usedGB = TotalGB * ramUsedPercent / 100;
        return new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(uptimeDays), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(TotalGB, TotalGB - usedGB, usedGB, ramUsedPercent, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6.9)]
    [InlineData(7)]
    [InlineData(7.01)]
    [InlineData(10)]
    [InlineData(13.9)]
    [InlineData(14)]
    [InlineData(30)]
    public void TheTuneUpCard_WarnsAboutUptime_ExactlyWhenTheHealthScoreRecommendsARestart(double days)
    {
        var tuneUp = new TuneUpResult { Uptime = TimeSpan.FromDays(days) };
        var recommended = HealthScoreService.ComputeUptimeScore(Snapshot(uptimeDays: days))
                          <= HealthScoreService.RestartRecommendedAtOrBelow;

        Assert.Equal(recommended, tuneUp.UptimeWarning);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(79.9)]
    [InlineData(80)]
    [InlineData(80.5)]
    [InlineData(84.9)]
    [InlineData(85)]
    [InlineData(95)]
    public void TheTuneUpCard_WarnsAboutMemory_ExactlyWhenTheHealthScoreRecommendsClosingApps(double usedPercent)
    {
        var tuneUp = new TuneUpResult { RamUsedPercent = usedPercent };
        var recommended = HealthScoreService.Evaluate(Snapshot(ramUsedPercent: usedPercent), null, null, null, null).Recommendations
            .Any(r => r.Message.StartsWith("High memory usage", StringComparison.Ordinal));

        Assert.Equal(recommended, tuneUp.RamWarning);
    }

    [Fact]
    public void TenDaysUp_BothCardsAdviseARestart()
    {
        // The case from the report: nothing else found, and the card said "this PC has restarted recently".
        Assert.True(new TuneUpResult { Uptime = TimeSpan.FromDays(10) }.UptimeWarning);
        Assert.Contains(HealthScoreService.Evaluate(Snapshot(uptimeDays: 10), null, null, null, null).Recommendations,
                        r => r.Message.StartsWith("Restart recommended", StringComparison.Ordinal));
    }
}
