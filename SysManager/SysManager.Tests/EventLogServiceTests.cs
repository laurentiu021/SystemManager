// SysManager · EventLogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EventLogService"/> — focuses on the pure-logic
/// BuildXPath and MapLevel methods that don't require the Windows Event Log.
/// </summary>
public class EventLogServiceTests
{
    // ---------- BuildXPath ----------

    private static string InvokeBuildXPath(EventLogQueryOptions opt)
    {
        var m = typeof(EventLogService).GetMethod("BuildXPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { opt })!;
    }

    [Fact]
    public void BuildXPath_NoFilters_ReturnsStar()
    {
        var result = InvokeBuildXPath(new EventLogQueryOptions());
        Assert.Equal("*", result);
    }

    [Fact]
    public void BuildXPath_WithSeverity_IncludesLevel()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new List<EventSeverity> { EventSeverity.Error }
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=2", result);
    }

    [Fact]
    public void BuildXPath_MultipleSeverities_IncludesOr()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new List<EventSeverity> { EventSeverity.Error, EventSeverity.Warning }
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=2", result);
        Assert.Contains("Level=3", result);
        Assert.Contains(" or ", result);
    }

    [Fact]
    public void BuildXPath_WithSince_IncludesTimeCreated()
    {
        var opt = new EventLogQueryOptions
        {
            Since = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc)
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("TimeCreated", result);
        Assert.Contains("2026-01-15", result);
    }

    [Fact]
    public void BuildXPath_WithProvider_IncludesProviderName()
    {
        var opt = new EventLogQueryOptions { ProviderName = "disk" };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Provider[@Name='disk']", result);
    }

    [Fact]
    public void BuildXPath_WithEventId_IncludesEventID()
    {
        var opt = new EventLogQueryOptions { EventId = 7 };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("EventID=7", result);
    }

    [Fact]
    public void BuildXPath_AllFilters_CombinesWithAnd()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new List<EventSeverity> { EventSeverity.Critical },
            Since = DateTime.UtcNow.AddDays(-7),
            ProviderName = "disk",
            EventId = 11
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains(" and ", result);
        Assert.Contains("Level=1", result);
        Assert.Contains("Provider[@Name='disk']", result);
        Assert.Contains("EventID=11", result);
    }

    [Fact]
    public void BuildXPath_ProviderWithQuotes_SanitizesInput()
    {
        var opt = new EventLogQueryOptions { ProviderName = "test'injection" };
        var result = InvokeBuildXPath(opt);
        Assert.DoesNotContain("'injection", result);
        Assert.Contains("testinjection", result);
    }

    // ---------- MapLevel ----------

    private static EventSeverity InvokeMapLevel(byte? level)
    {
        var m = typeof(EventLogService).GetMethod("MapLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (EventSeverity)m.Invoke(null, new object?[] { level })!;
    }

    [Theory]
    [InlineData((byte)1, EventSeverity.Critical)]
    [InlineData((byte)2, EventSeverity.Error)]
    [InlineData((byte)3, EventSeverity.Warning)]
    [InlineData((byte)4, EventSeverity.Info)]
    [InlineData((byte)5, EventSeverity.Verbose)]
    public void MapLevel_KnownLevels_ReturnCorrectSeverity(byte level, EventSeverity expected)
        => Assert.Equal(expected, InvokeMapLevel(level));

    [Fact]
    public void MapLevel_Null_ReturnsInfo()
        => Assert.Equal(EventSeverity.Info, InvokeMapLevel(null));

    [Fact]
    public void MapLevel_UnknownValue_ReturnsInfo()
        => Assert.Equal(EventSeverity.Info, InvokeMapLevel((byte)99));

    // ---------- SeverityToLevel ----------

    private static byte InvokeSeverityToLevel(EventSeverity s)
    {
        var m = typeof(EventLogService).GetMethod("SeverityToLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (byte)m.Invoke(null, new object[] { s })!;
    }

    [Theory]
    [InlineData(EventSeverity.Critical, (byte)1)]
    [InlineData(EventSeverity.Error, (byte)2)]
    [InlineData(EventSeverity.Warning, (byte)3)]
    [InlineData(EventSeverity.Info, (byte)4)]
    [InlineData(EventSeverity.Verbose, (byte)5)]
    public void SeverityToLevel_RoundTrips(EventSeverity severity, byte expected)
        => Assert.Equal(expected, InvokeSeverityToLevel(severity));

    // ---------- EventLogQueryOptions defaults ----------

    [Fact]
    public void QueryOptions_DefaultLogName_IsSystem()
    {
        var opt = new EventLogQueryOptions();
        Assert.Equal("System", opt.LogName);
    }

    [Fact]
    public void QueryOptions_DefaultMaxResults_Is500()
    {
        var opt = new EventLogQueryOptions();
        Assert.Equal(500, opt.MaxResults);
    }

    [Fact]
    public void QueryOptions_DefaultSeverities_IsNull()
    {
        var opt = new EventLogQueryOptions();
        Assert.Null(opt.Severities);
    }
}
