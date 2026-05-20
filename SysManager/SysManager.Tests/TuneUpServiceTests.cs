// SysManager · TuneUpServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="TuneUpService"/> — validates orchestration logic,
/// progress reporting, and result aggregation. Actual system calls (WMI, file I/O)
/// are integration-level; these tests verify the service wires steps correctly.
/// </summary>
public class TuneUpServiceTests
{
    private static TuneUpService CreateService()
        => new(new ShortcutCleanerService(), new DiskHealthService(), new SystemInfoService());

    // ---------- construction ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = CreateService();
        Assert.NotNull(svc);
    }

    // ---------- TuneUpResult model ----------

    [Fact]
    public void TuneUpResult_FreedDisplay_FormatsCorrectly()
    {
        var result = new TuneUpResult { TempBytesFreed = 1024 * 1024 * 50 }; // 50 MB
        Assert.Contains("MB", result.FreedDisplay);
    }

    [Fact]
    public void TuneUpResult_FreedDisplay_ZeroBytes()
    {
        var result = new TuneUpResult { TempBytesFreed = 0 };
        Assert.Equal("0 B", result.FreedDisplay);
    }

    [Fact]
    public void TuneUpResult_UptimeWarning_FalseUnder14Days()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(13) };
        Assert.False(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_UptimeWarning_TrueAt14Days()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(14) };
        Assert.True(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_UptimeWarning_TrueOver14Days()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(30) };
        Assert.True(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_FalseUnder85()
    {
        var result = new TuneUpResult { RamUsedPercent = 60 };
        Assert.False(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_TrueAt85()
    {
        var result = new TuneUpResult { RamUsedPercent = 85 };
        Assert.True(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_TrueOver85()
    {
        var result = new TuneUpResult { RamUsedPercent = 95 };
        Assert.True(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_WarningCount_ZeroWhenAllGood()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Healthy", ColorHex = "#22C55E" }
            }
        };
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsBrokenShortcuts()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 5,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsUptime()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsRam()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 90,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsUnhealthyDisk()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Warning", ColorHex = "#F59E0B" }
            }
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_MultipleWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 3,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 92,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Warning", ColorHex = "#F59E0B" },
                new() { Name = "Disk1", Verdict = "Healthy", ColorHex = "#22C55E" }
            }
        };
        // shortcuts(1) + uptime(1) + ram(1) + disk0(1) = 4
        Assert.Equal(4, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_AllGood()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("All good", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_OneRecommendation()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("1 recommendation", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_MultipleRecommendations()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("2 recommendations", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_GreenWhenNoWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("#22C55E", result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_OrangeFor1Or2Warnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("#F59E0B", result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_RedFor3PlusWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 92,
            DiskResults = []
        };
        Assert.Equal("#EF4444", result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_RecycleBinSkipped_DefaultFalse()
    {
        var result = new TuneUpResult();
        Assert.False(result.RecycleBinSkipped);
    }

    [Fact]
    public void TuneUpResult_RecycleBinEmptied_DefaultFalse()
    {
        var result = new TuneUpResult();
        Assert.False(result.RecycleBinEmptied);
    }

    [Fact]
    public void DiskHealthSummary_Properties_SetCorrectly()
    {
        var summary = new DiskHealthSummary
        {
            Name = "Samsung 980 Pro",
            Verdict = "Healthy",
            ColorHex = "#22C55E"
        };
        Assert.Equal("Samsung 980 Pro", summary.Name);
        Assert.Equal("Healthy", summary.Verdict);
        Assert.Equal("#22C55E", summary.ColorHex);
    }
}
