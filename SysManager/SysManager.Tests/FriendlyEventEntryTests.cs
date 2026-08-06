// SysManager · FriendlyEventEntryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class FriendlyEventEntryTests
{
    [Theory]
    [InlineData(EventSeverity.Critical, "⛔")]
    [InlineData(EventSeverity.Error, "🔴")]
    [InlineData(EventSeverity.Warning, "🟡")]
    [InlineData(EventSeverity.Info, "🔵")]
    [InlineData(EventSeverity.Verbose, "⚪")]
    public void SeverityIcon_MapsCorrectly(EventSeverity sev, string icon)
    {
        var e = new FriendlyEventEntry { Severity = sev };
        Assert.Equal(icon, e.SeverityIcon);
    }

    [Theory]
    [InlineData(EventSeverity.Critical)]
    [InlineData(EventSeverity.Error)]
    [InlineData(EventSeverity.Warning)]
    [InlineData(EventSeverity.Info)]
    [InlineData(EventSeverity.Verbose)]
    public void SeverityColor_IsAThemeResourceKey(EventSeverity sev)
    {
        // This used to assert a hex literal. Severity colours now name a theme brush so they follow
        // the active preset — a hex here would be a colour ThemeService cannot repaint, which on a
        // light theme left the severity dot pale on a near-white row.
        var e = new FriendlyEventEntry { Severity = sev };
        Assert.DoesNotMatch("^#", e.SeverityColor);
        Assert.False(string.IsNullOrWhiteSpace(e.SeverityColor));
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var e = new FriendlyEventEntry();
        Assert.Equal("", e.LogName);
        Assert.Equal("", e.ProviderName);
        Assert.Equal(0, e.EventId);
        Assert.Equal("", e.Message);
        Assert.Equal("", e.FullMessage);
    }
}
