// SysManager · LiveRegionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// When <see cref="LiveRegion"/> asks for a line to be announced, and when an announcement it asked for is heard
/// (#2670).
/// </summary>
/// <remarks>
/// A screen reader speaks a live region only when the app raises <c>LiveRegionChanged</c> for it, and WPF never raises
/// it on its own. The event goes out in a later dispatcher pass, to a screen reader that is listening, for a line on
/// screen, none of which a test without a window has. So these pin what decides it: that a change to a marked, loaded
/// line's text asks for an announcement and nothing else does, and which lines are heard when it goes out. A line
/// counts as loaded here through <c>LiveRegion.LoadedForTestProperty</c>, since nothing loads without a window.
/// </remarks>
public class LiveRegionTests
{
    private static TextBlock MarkedLine(string text, bool loaded = true)
    {
        var line = new TextBlock { Text = text };
        line.SetValue(LiveRegion.LoadedForTestProperty, loaded);
        LiveRegion.SetAnnounces(line, true);
        return line;
    }

    [StaFact]
    public void AMarkedLine_AsksToBeAnnounced_WhenItsTextChanges()
    {
        var line = MarkedLine("Click Scan to start.");

        line.Text = "Scanning…";

        Assert.NotNull(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void TheTextAMarkedLineOpensWith_IsNotAnnounced()
    {
        var line = MarkedLine("Click Scan to start.");

        Assert.Null(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void AChangeBeforeTheLineIsLoaded_IsNotAnnounced()
    {
        // The status a tab's view model sets while its view is being built is what the tab shows when it opens.
        var line = MarkedLine("", loaded: false);

        line.Text = "Click Scan to start.";

        Assert.Null(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void AMarkedLineEmptied_IsNotAnnounced()
    {
        var line = MarkedLine("Scan complete.");

        line.Text = "";

        Assert.Null(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void ALineNotMarked_IsNotAnnounced()
    {
        var line = new TextBlock { Text = "Click Scan to start." };
        line.SetValue(LiveRegion.LoadedForTestProperty, true);

        line.Text = "Scanning…";

        Assert.Null(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void ALineUnmarked_DropsTheAnnouncementItAskedFor_AndAsksForNoMore()
    {
        var line = MarkedLine("Click Scan to start.");
        line.Text = "Scanning…";
        var asked = LiveRegion.PendingOf(line);

        LiveRegion.SetAnnounces(line, false);
        line.Text = "Scan complete.";

        Assert.Equal(DispatcherOperationStatus.Aborted, asked?.Status);
        Assert.Null(LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void SeveralChangesInOnePass_AskForOneAnnouncement()
    {
        var line = MarkedLine("Click Scan to start.");
        line.Text = "Scanning…";
        var first = LiveRegion.PendingOf(line);

        line.Text = "Scanning C:\\Users…";
        line.Text = "Scan complete.";

        Assert.NotNull(first);
        Assert.Same(first, LiveRegion.PendingOf(line));
    }

    [StaFact]
    public void TheSameWordsShownAgain_AreAnnouncedWhenAsked()
    {
        // The toast: a second "Dashboard refreshed" leaves the text as it was, so it is asked for outright.
        var line = MarkedLine("Dashboard refreshed");

        LiveRegion.Announce(line);

        Assert.NotNull(LiveRegion.PendingOf(line));
    }

    [Theory]
    [InlineData("Scanning…", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsNews_IsAChangeToSomethingToSay(string? text, bool news) =>
        Assert.Equal(news, LiveRegion.IsNews(text));

    [Theory]
    [InlineData(AutomationLiveSetting.Polite, true, true)]
    [InlineData(AutomationLiveSetting.Assertive, true, true)]
    [InlineData(AutomationLiveSetting.Off, true, false)]
    [InlineData(AutomationLiveSetting.Polite, false, false)]
    public void IsHeard_IsALiveLineOnScreen(AutomationLiveSetting setting, bool shown, bool heard) =>
        Assert.Equal(heard, LiveRegion.IsHeard(setting, shown));
}
