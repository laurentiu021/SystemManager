// SysManager · WelcomeOverlayUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Welcome screen really builds and draws (#1635): the real XAML with the app's dictionaries, its buttons bound to
/// the view model's commands, and the screen announced as a dialog named for its title.
/// </summary>
/// <remarks>
/// The unit tests prove what the view model does and read the markup's attributes. They cannot prove that a resource the
/// screen names exists: a missing one compiles and throws when the window opens, on a first launch, which is the only
/// launch that shows the screen at all. So this builds it, as <see cref="RecentChangesViewUiTests"/> builds its tab. The
/// record and the navigation are fakes written here, so nothing on this PC is read or written.
/// </remarks>
[Collection("Network")]
public sealed class WelcomeOverlayUiTests
{
    private sealed class FakeFirstRun : IFirstRunService
    {
        public int Marked { get; private set; }

        public bool HasSeenWelcome() => false;

        public bool MarkWelcomeSeen()
        {
            Marked++;
            return true;
        }
    }

    private sealed class RecordingNavigation : INavigationService
    {
        public List<string> Visited { get; } = [];

        public void GoTo(string navId, string? filter = null, Models.NavigationArrival? arrival = null) =>
            Visited.Add(navId);
    }

    [Fact]
    public void TheScreen_Builds_AndEachButtonRunsItsOwnCommand()
    {
        var firstRun = new FakeFirstRun();
        var navigation = new RecordingNavigation();

        StaHelper.Run(() =>
        {
            var welcome = new WelcomeViewModel(firstRun, navigation);
            var overlay = Laid(welcome);

            var buttons = Descendants<Button>(overlay).ToList();
            Assert.Equal(["Check my PC's health", "Clean up junk files", "Just look around"], buttons.Select(b => b.Content as string));
            Assert.Same(welcome.CheckHealthCommand, buttons[0].Command);
            Assert.Same(welcome.CleanUpCommand, buttons[1].Command);
            Assert.Same(welcome.LookAroundCommand, buttons[2].Command);

            buttons[0].Command.Execute(null);

            Assert.False(welcome.IsVisible);
            Assert.Equal(["nav-system-health"], navigation.Visited);
            Assert.Equal(1, firstRun.Marked);
        });
    }

    [Fact]
    public void TheScreen_IsADialogNamedForTheTitleItShows()
    {
        StaHelper.Run(() =>
        {
            var overlay = Laid(new WelcomeViewModel(new FakeFirstRun(), new RecordingNavigation()));

            // A UserControl has an automation peer, which a Border does not: without one the dialog role and the name
            // reach no screen reader.
            var peer = UIElementAutomationPeer.CreatePeerForElement(overlay);
            Assert.NotNull(peer);
            Assert.True(peer.IsDialog(), "the Welcome screen is not announced as a dialog");
            Assert.Equal("Welcome to SysManager", peer.GetName());

            var shown = ShownTexts(overlay);
            Assert.Equal("Welcome to SysManager", shown[0]);
            Assert.Contains(shown, t => t.Contains("plus two checks when it starts", StringComparison.Ordinal));
        });
    }

    private static WelcomeOverlay Laid(WelcomeViewModel welcome)
    {
        AppResources.Ensure();
        var overlay = new WelcomeOverlay { DataContext = welcome };
        overlay.Measure(new Size(1280, 820));
        overlay.Arrange(new Rect(0, 0, 1280, 820));
        overlay.UpdateLayout();
        return overlay;
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order, icon glyphs left out.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && !IsGlyph(block.Text)) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
