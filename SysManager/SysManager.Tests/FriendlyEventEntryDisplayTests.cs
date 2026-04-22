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
}
