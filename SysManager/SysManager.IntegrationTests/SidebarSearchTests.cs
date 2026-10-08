// SysManager · SidebarSearchTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Search over the 60 tabs, and the keywords that make it work for someone who does not know the
/// vocabulary (#1498, #1505).
/// </summary>
/// <remarks>
/// In the integration project because it needs the real shell, which builds the whole nav graph — the
/// unit suite cannot construct <c>MainWindowViewModel</c> (its About tab runs a startup update check).
/// <para>Driven through the real <c>NavFilter</c> property rather than a copy of the matching rule, so a
/// tab added without keywords, or a filter that stops trimming, fails here rather than shipping.</para>
/// </remarks>
[Collection("Network")]
public class SidebarSearchTests(NavSurfaceFixture fixture) : IClassFixture<NavSurfaceFixture>
{
    // The searches below only READ the nav surface — but NavFilter is state on the shell, so each test
    // clears it afterwards. See NavSurfaceFixture for why one graph is shared at all.
    private readonly MainWindowViewModel _nav = fixture.Vm;

    private List<string> Search(string text)
    {
        try
        {
            _nav.NavFilter = text;
            return _nav.NavResults.Select(r => r.Id).ToList();
        }
        finally
        {
            _nav.NavFilter = "";
        }
    }

    [Fact]
    public void NoSearch_LeavesTheTreeShowingAndTheResultsEmpty()
    {
        Assert.False(_nav.IsSearchingNav);
        Assert.Empty(_nav.NavResults);
        Assert.Equal("", _nav.NavResultSummary);
    }

    [Fact]
    public void SearchingByLabel_FindsTheTab()
    {
        Assert.Contains("nav-disk-analyzer", Search("Disk Analyzer"));
    }

    /// <summary>
    /// The half that makes search usable: plain words nobody would find in the label.
    /// </summary>
    /// <remarks>
    /// Each of these is a phrase the app's own issue tracker recorded as what this persona actually types.
    /// None of them appears in the label of the tab that answers it — which is the whole reason keywords
    /// exist rather than a label-only match.
    /// </remarks>
    [Theory]
    [InlineData("slow startup", "nav-boot-analyzer")]
    [InlineData("popups", "nav-notification-blocker")]
    [InlineData("webcam", "nav-privacy-monitor")]
    [InlineData("free up space", "nav-deep-cleanup")]
    [InlineData("ram", "nav-standby-cleaner")]
    [InlineData("stutter", "nav-timer-resolution")]
    [InlineData("control panel", "nav-legacy-panels")]
    [InlineData("task manager", "nav-processes")]
    [InlineData("cannot delete", "nav-file-lock")]
    [InlineData("no internet", "nav-network-repair")]
    [InlineData("speakers", "nav-volume-control")]
    [InlineData("headphones", "nav-volume-control")]
    [InlineData("extensions", "nav-browser-cleaner")]
    [InlineData("add-ons", "nav-browser-cleaner")]
    [InlineData("browser ads", "nav-browser-cleaner")]
    [InlineData("put back", "nav-undo-changes")]
    [InlineData("revert", "nav-undo-changes")]
    [InlineData("what changed", "nav-recent-changes")]
    [InlineData("it was fine last week", "nav-recent-changes")]
    public void SearchingByPlainWords_FindsTheJargonNamedTab(string typed, string expectedNavId)
    {
        var results = Search(typed);

        Assert.Contains(expectedNavId, results);
        // The phrase must not be in the label, or this would pass without any keyword at all.
        var label = _nav.NavItems.First(n => n.Id == expectedNavId).Label;
        Assert.DoesNotContain(typed, label, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tweaks")]
    [InlineData("tune windows")]
    public void ASearchThatUsedToFindTweaksHub_OpensPrivacyByReach(string typed)
    {
        // Tweaks Hub listed exactly Privacy & Telemetry's switches, grouped by reach, and that grouping moved there
        // with its search words (#1517).
        var privacy = (PrivacyViewModel)_nav.NavItems.First(n => n.Id == "nav-privacy-settings").Content;
        var previous = _nav.SelectedNav;
        try
        {
            privacy.Grouping = Models.PrivacyGrouping.ByTopic;
            _nav.NavFilter = typed;

            _nav.SelectedNav = Assert.Single(_nav.NavResults, r => r.Id == "nav-privacy-settings");

            Assert.Equal(Models.PrivacyGrouping.ByReach, privacy.Grouping);
        }
        finally
        {
            _nav.NavFilter = "";
            _nav.SelectedNav = previous;
            privacy.Grouping = Models.PrivacyGrouping.ByTopic;
        }
    }

    [Fact]
    public void OpeningPrivacyFromAnyOtherSearch_KeepsItsGrouping()
    {
        var privacy = (PrivacyViewModel)_nav.NavItems.First(n => n.Id == "nav-privacy-settings").Content;
        var previous = _nav.SelectedNav;
        try
        {
            privacy.Grouping = Models.PrivacyGrouping.ByTopic;
            _nav.NavFilter = "telemetry";

            _nav.SelectedNav = Assert.Single(_nav.NavResults, r => r.Id == "nav-privacy-settings");

            Assert.Equal(Models.PrivacyGrouping.ByTopic, privacy.Grouping);
        }
        finally
        {
            _nav.NavFilter = "";
            _nav.SelectedNav = previous;
        }
    }

    [Theory]
    [InlineData("extensions")]
    [InlineData("browser ads")]
    public void ASearchForAnExtension_OpensBrowserCleanerOnItsExtensions(string typed)
    {
        // "My searches go to some other site" is an extension far more often than it is the cache (#1526).
        var cleaner = (BrowserCleanerViewModel)_nav.NavItems.First(n => n.Id == "nav-browser-cleaner").Content;
        var previous = _nav.SelectedNav;
        try
        {
            cleaner.Section = BrowserCleanerSection.BrowsingData;
            _nav.NavFilter = typed;

            _nav.SelectedNav = Assert.Single(_nav.NavResults, r => r.Id == "nav-browser-cleaner");

            Assert.Equal(BrowserCleanerSection.Extensions, cleaner.Section);
        }
        finally
        {
            _nav.NavFilter = "";
            _nav.SelectedNav = previous;
            cleaner.Section = BrowserCleanerSection.BrowsingData;
        }
    }

    [Fact]
    public void OpeningBrowserCleanerFromAnyOtherSearch_StaysOnTheBrowsingData()
    {
        var cleaner = (BrowserCleanerViewModel)_nav.NavItems.First(n => n.Id == "nav-browser-cleaner").Content;
        var previous = _nav.SelectedNav;
        try
        {
            cleaner.Section = BrowserCleanerSection.BrowsingData;
            _nav.NavFilter = "cookies";

            _nav.SelectedNav = Assert.Single(_nav.NavResults, r => r.Id == "nav-browser-cleaner");

            Assert.Equal(BrowserCleanerSection.BrowsingData, cleaner.Section);
        }
        finally
        {
            _nav.NavFilter = "";
            _nav.SelectedNav = previous;
        }
    }

    [Fact]
    public void SearchingIsCaseInsensitive()
    {
        Assert.Equal(Search("WEBCAM"), Search("webcam"));
    }

    [Fact]
    public void SearchTextIsTrimmed()
    {
        // A pasted term arrives with whitespace often enough that not trimming reads as search being broken.
        Assert.Equal(Search("webcam"), Search("  webcam  "));
    }

    [Fact]
    public void AMatchlessSearch_SaysSoRatherThanShowingAnEmptyPanel()
    {
        _nav.NavFilter = "zzzz-nothing-matches-this";
        try
        {
            Assert.True(_nav.IsSearchingNav);
            Assert.Empty(_nav.NavResults);
            Assert.Contains("No tabs match", _nav.NavResultSummary, StringComparison.Ordinal);
        }
        finally { _nav.NavFilter = ""; }
    }

    [Fact]
    public void TheSummaryCountsWhatTheListShows()
    {
        _nav.NavFilter = "cleanup";
        try
        {
            var expected = _nav.NavResults.Count == 1 ? "1 tab" : $"{_nav.NavResults.Count} tabs";
            Assert.Equal(expected, _nav.NavResultSummary);
            Assert.True(_nav.NavResults.Count > 0, "\"cleanup\" matching nothing would make this vacuous.");
        }
        finally { _nav.NavFilter = ""; }
    }

    [Fact]
    public void ClearingTheSearch_RestoresTheTree()
    {
        _nav.NavFilter = "webcam";
        Assert.True(_nav.IsSearchingNav);

        _nav.ClearNavFilterCommand.Execute(null);

        Assert.False(_nav.IsSearchingNav);
        Assert.Empty(_nav.NavResults);
        Assert.Equal("", _nav.NavFilter);
    }

    /// <summary>
    /// Searching must not build a single tab's view model.
    /// </summary>
    /// <remarks>
    /// The results bind to <c>NavItem</c>, and reading <c>NavItem.Content</c> CONSTRUCTS the view model —
    /// so a filter that touched Content would build every matching tab on every keystroke, undoing the
    /// lazy-startup fix that exists because ~40 tabs start a scan or a timer in their constructor.
    /// <para>Asserted on the DI path's own flag rather than by timing: in this test project there is no
    /// container, so every tab is eager and <c>IsContentCreated</c> is already true. What this pins is that
    /// the filter reads Label and Keywords — properties, not Content — which is checkable either way.
    /// </para>
    /// </remarks>
    [Fact]
    public void SearchingReadsOnlyTheRowsOwnText()
    {
        var item = _nav.NavItems.First(n => n.Id == "nav-boot-analyzer");

        Assert.True(item.Matches("slow startup"));
        Assert.True(item.Matches("Boot Analyzer"));
        Assert.False(item.Matches("zzzz"));
    }
}
