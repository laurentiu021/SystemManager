// SysManager · FriendlyEventEntryDisplayTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FriendlyEventEntry"/> display properties
/// (SeverityIcon, SeverityColor) and the <see cref="EventSeverity"/> enum.
/// </summary>
public class FriendlyEventEntryDisplayTests
{
    [Theory]
    [InlineData(EventSeverity.Critical, "⛔")]
    [InlineData(EventSeverity.Error, "🔴")]
    [InlineData(EventSeverity.Warning, "🟡")]
    [InlineData(EventSeverity.Info, "🔵")]
    [InlineData(EventSeverity.Verbose, "⚪")]
    public void SeverityIcon_ReturnsExpected(EventSeverity severity, string expected)
    {
        var entry = new FriendlyEventEntry { Severity = severity };
        Assert.Equal(expected, entry.SeverityIcon);
    }

    [Theory]
    [InlineData(EventSeverity.Critical, "#FF3B30")]
    [InlineData(EventSeverity.Error, "#FF6B6B")]
    [InlineData(EventSeverity.Warning, "#FFD166")]
    [InlineData(EventSeverity.Info, "#4CC9F0")]
    [InlineData(EventSeverity.Verbose, "#9AA0A6")]
    public void SeverityColor_ReturnsExpected(EventSeverity severity, string expected)
    {
        var entry = new FriendlyEventEntry { Severity = severity };
        Assert.Equal(expected, entry.SeverityColor);
    }

    [Fact]
    public void DefaultEntry_HasEmptyStrings()
    {
        var entry = new FriendlyEventEntry();
        Assert.Equal("", entry.LogName);
        Assert.Equal("", entry.ProviderName);
        Assert.Equal("", entry.Message);
        Assert.Equal("", entry.FullMessage);
        Assert.Equal("", entry.Xml);
        Assert.Equal("", entry.Explanation);
        Assert.Equal("", entry.Recommendation);
    }

    [Fact]
    public void Entry_SupportsPropertyChange()
    {
        var entry = new FriendlyEventEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Explanation = "Test explanation";
        entry.Recommendation = "Test recommendation";

        Assert.Contains("Explanation", changed);
        Assert.Contains("Recommendation", changed);
    }

    // ---------- RelativeTime ----------

    [Fact]
    public void RelativeTime_JustNow()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.Now.AddSeconds(-30) };
        Assert.Equal("just now", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_MinutesAgo()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.Now.AddMinutes(-15) };
        Assert.Equal("15 min ago", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_HoursAgo()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.Now.AddHours(-3) };
        Assert.Equal("3h ago", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_DaysAgo()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.Now.AddDays(-2) };
        Assert.Equal("2d ago", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_WeeksAgo()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.Now.AddDays(-14) };
        Assert.Equal("2w ago", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_OldDate_ShowsDate()
    {
        var entry = new FriendlyEventEntry { Timestamp = new DateTime(2025, 1, 15) };
        Assert.Equal("2025-01-15", entry.RelativeTime);
    }

    [Fact]
    public void RelativeTime_MinValue_ShowsDash()
    {
        var entry = new FriendlyEventEntry { Timestamp = DateTime.MinValue };
        Assert.Equal("—", entry.RelativeTime);
    }

    // ---------- FullTimestamp ----------

    [Fact]
    public void FullTimestamp_FormatsCorrectly()
    {
        var entry = new FriendlyEventEntry { Timestamp = new DateTime(2026, 4, 25, 14, 30, 45) };
        Assert.Equal("2026-04-25 14:30:45", entry.FullTimestamp);
    }
}
