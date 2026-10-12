// SysManager · WelcomeOverlayContractTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SysManager.Tests;

/// <summary>
/// How the shell shows the one-time Welcome screen (#1635), read from the shipped markup and code-behind.
/// </summary>
/// <remarks>
/// <c>WelcomeViewModelTests</c> prove what the screen does when it is asked. None of them can see whether it covers the
/// window, whether the window under it is closed off, which button takes focus, or whether closing the window records
/// it: all of that lives in XAML and code-behind, and a mistake in any of it compiles and leaves every view-model test
/// green.
/// </remarks>
public partial class WelcomeOverlayContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string DisabledWhileTheWelcomeIsUp = "{Binding Welcome.IsVisible, Converter={StaticResource BoolInvert}}";

    /// <summary>The window's root grid's children, in the order they are drawn, without its column definitions.</summary>
    private static List<XElement> ShellLayers()
    {
        var window = XDocument.Load(TestPaths.AppFile("MainWindow.xaml")).Root!;
        return [.. window.Elements(Presentation + "Grid").Single().Elements().Where(e => !e.Name.LocalName.Contains('.'))];
    }

    private static XElement Overlay() => XDocument.Load(TestPaths.AppFile("Views", "WelcomeOverlay.xaml")).Root!;

    [Fact]
    public void TheWelcome_IsDrawnOverBothColumns_AndUnderTheToast()
    {
        var layers = ShellLayers();
        var sidebar = layers.FindIndex(e => (string?)e.Attribute("Grid.Column") == "0");
        var page = layers.FindIndex(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ContentHost");
        var welcome = layers.FindIndex(e => e.Name.LocalName == "WelcomeOverlay");
        var toast = layers.FindIndex(e => (string?)e.Attribute(Xaml + "Name") == "ToastOverlay");
        Assert.True(sidebar >= 0 && page >= 0 && welcome >= 0 && toast >= 0,
            $"a layer of the window was not found (sidebar={sidebar}, page={page}, welcome={welcome}, toast={toast})");

        Assert.True(welcome > sidebar && welcome > page, "the Welcome screen is drawn under the sidebar or the page");
        Assert.True(welcome < toast, "the Welcome screen is drawn over the notification in the corner");

        var overlay = layers[welcome];
        Assert.Equal("2", (string?)overlay.Attribute("Grid.ColumnSpan"));
        Assert.Equal("{Binding Welcome}", (string?)overlay.Attribute("DataContext"));
        Assert.Equal("{Binding IsVisible, Converter={StaticResource FlexVis}}", (string?)overlay.Attribute("Visibility"));
    }

    /// <summary>
    /// Covering the window is not closing it off: UI Automation, which screen readers and voice control use, invokes a
    /// control through anything drawn on top of it.
    /// </summary>
    [Fact]
    public void TheSidebarAndThePage_AreDisabledWhileTheWelcomeIsUp()
    {
        var layers = ShellLayers();
        var sidebar = layers.Single(e => (string?)e.Attribute("Grid.Column") == "0");
        var page = layers.Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ContentHost");

        Assert.Equal(DisabledWhileTheWelcomeIsUp, (string?)sidebar.Attribute("IsEnabled"));
        Assert.Equal(DisabledWhileTheWelcomeIsUp, (string?)page.Attribute("IsEnabled"));
    }

    [Fact]
    public void TheWelcome_IsADialog_NamedForItsTitle_AndKeepsTabInside()
    {
        var overlay = Overlay();

        // A UserControl, because a Border has no automation peer and the two properties below would reach nobody.
        Assert.Equal(Presentation + "UserControl", overlay.Name);
        Assert.Equal("True", (string?)overlay.Attribute("AutomationProperties.IsDialog"));
        Assert.Equal("WelcomeOverlay", (string?)overlay.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("Cycle", (string?)overlay.Attribute("KeyboardNavigation.TabNavigation"));

        var title = Assert.Single(overlay.Descendants(Presentation + "TextBlock"),
            t => (string?)t.Attribute("Style") == "{StaticResource Heading}");
        Assert.Equal((string?)title.Attribute("Text"), (string?)overlay.Attribute("AutomationProperties.Name"));
    }

    /// <summary>
    /// Three ways out, in tab order, and one primary: the safest first step, which is also where focus starts. Three
    /// purple buttons would say nothing about where to begin.
    /// </summary>
    [Fact]
    public void TheWelcome_OffersThreeWaysOut_TheFirstPrimaryAndFocused()
    {
        var buttons = Overlay().Descendants(Presentation + "Button").ToList();

        Assert.Equal(
            [
                ("{Binding CheckHealthCommand}", "{StaticResource PrimaryButton}", "WelcomeCheckHealth"),
                ("{Binding CleanUpCommand}", "{StaticResource SecondaryButton}", "WelcomeCleanUp"),
                ("{Binding LookAroundCommand}", "{StaticResource GhostButton}", "WelcomeLookAround"),
            ],
            buttons.Select(b => ((string?)b.Attribute("Command"), (string?)b.Attribute("Style"),
                                 (string?)b.Attribute("AutomationProperties.AutomationId"))));

        Assert.Equal("CheckHealthButton", (string?)buttons[0].Attribute(Xaml + "Name"));
        var codeBehind = File.ReadAllText(TestPaths.AppFile("Views", "WelcomeOverlay.xaml.cs"));
        Assert.Contains("CheckHealthButton.Focus()", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence about the network says what the app does online on its own, both halves: the version check with
    /// how often it runs and where it is switched off, and the app-update check. A first draft named only the version
    /// check, as "the only thing" the app sends, while the Dashboard asks winget at every launch.
    /// <c>ArchitectureTests.EveryCountOfTheChecksAtStartup_IsTheOneSecurityMdGives</c> holds its count to SECURITY.md.
    /// </summary>
    [Fact]
    public void TheNetworkSentence_NamesBothChecks_AndHowTheVersionCheckIsLimited()
    {
        var promise = Assert.Single(Overlay().Descendants(Presentation + "TextBlock"),
            t => (string?)t.Attribute(Xaml + "Name") == "NetworkPromise");
        var text = (string?)promise.Attribute("Text") ?? "";

        foreach (var part in new[] { "GitHub", "at most once a day", "About", "winget" })
            Assert.Contains(part, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing the window counts as seen, so the next launch does not show the screen again. Asked before the first
    /// return, since every return in the handler is a close: a call placed after the early exit for a programmatic
    /// shutdown would miss the tray's Exit, and one after the question would miss the answer that exits.
    /// </summary>
    [Fact]
    public void ClosingTheWindow_RecordsTheWelcomeAsSeen_BeforeItsFirstReturn()
    {
        var source = File.ReadAllText(TestPaths.AppFile("MainWindow.xaml.cs"));
        var start = source.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
        var end = start < 0 ? -1 : source.IndexOf("protected override void OnClosed", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"OnClosing was not found (start={start}, end={end}) — fix this test.");
        var body = LineComment().Replace(source[start..end], "");

        var remembered = body.IndexOf("Welcome.RememberSeen()", StringComparison.Ordinal);
        var firstReturn = body.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(remembered >= 0, "closing the window no longer records the Welcome screen as seen");
        Assert.True(firstReturn >= 0, "OnClosing has no return left — re-derive this test against what it does now.");
        Assert.True(remembered < firstReturn, "the Welcome screen is recorded after a return, so that way out misses it");
    }

    /// <summary>A <c>//</c> comment, to the end of its line.</summary>
    [GeneratedRegex(@"//[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();
}
