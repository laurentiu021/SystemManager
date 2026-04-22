// SysManager · FriendlyEventEntryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
    public void SeverityColor_IsValidHex(EventSeverity sev)
    {
        var e = new FriendlyEventEntry { Severity = sev };
        Assert.Matches("^#[0-9A-Fa-f]{6}$", e.SeverityColor);
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
