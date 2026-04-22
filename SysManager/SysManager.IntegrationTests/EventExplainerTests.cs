// SysManager · EventExplainerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class EventExplainerTests
{
    private static FriendlyEventEntry Make(string provider, int id, EventSeverity sev = EventSeverity.Error) => new()
    {
        ProviderName = provider,
        EventId = id,
        Severity = sev
    };

    [Fact]
    public void KnownProviderAndId_GetsSpecificExplanation()
    {
        var e = Make("Microsoft-Windows-Kernel-Power", 41, EventSeverity.Critical);
        EventExplainer.Enrich(e);
        Assert.Contains("rebooted unexpectedly", e.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(e.Recommendation);
    }

    [Fact]
    public void KnownFallbackById_StillExplained()
    {
        var e = Make("Some-Obscure-Provider", 6008);
        EventExplainer.Enrich(e);
        Assert.Contains("unexpected", e.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownEvent_GetsGenericExplanation_BasedOnSeverity()
    {
        var e = Make("Nobody-Cares", 99999, EventSeverity.Warning);
        EventExplainer.Enrich(e);
        Assert.NotEmpty(e.Explanation);
        Assert.NotEmpty(e.Recommendation);
        Assert.Contains("Nobody-Cares", e.Explanation);
    }

    [Theory]
    [InlineData(EventSeverity.Critical, "Critical")]
    [InlineData(EventSeverity.Error, "error")]
    [InlineData(EventSeverity.Warning, "warning")]
    [InlineData(EventSeverity.Info, "Informational")]
    public void GenericExplanation_MatchesSeverityWord(EventSeverity sev, string expectedWord)
    {
        var e = Make("X", 42, sev);
        EventExplainer.Enrich(e);
        Assert.Contains(expectedWord, e.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllKnownEntries_HaveNonEmptyTexts()
    {
        // Smoke: any known-table miss could leave empty strings; verify mappings.
        var pairs = new (string Provider, int Id)[]
        {
            ("Microsoft-Windows-Kernel-Power", 41),
            ("Application Error", 1000),
            ("disk", 7),
            ("Service Control Manager", 7034),
            ("Microsoft-Windows-Security-Auditing", 4625)
        };

        foreach (var (p, id) in pairs)
        {
            var e = Make(p, id);
            EventExplainer.Enrich(e);
            Assert.False(string.IsNullOrWhiteSpace(e.Explanation), $"Explanation empty for {p}/{id}");
            Assert.False(string.IsNullOrWhiteSpace(e.Recommendation), $"Recommendation empty for {p}/{id}");
        }
    }
}
