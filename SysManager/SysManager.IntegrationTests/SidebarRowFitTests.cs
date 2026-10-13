// SysManager · SidebarRowFitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every sidebar name fits its row, measured by WPF in the real window, with the tree long enough to scroll (#2686).
/// </summary>
/// <remarks>
/// The guard this replaces counted characters against a width it took as 178 px. Laid out, the rail leaves 155 px for
/// a row's content and a PREVIEW pill beside the name took 56 of them, so three PREVIEW names were trimmed while it
/// passed. Only layout can say whether text fits, so this builds the real <see cref="MainWindow"/> on the STA thread,
/// gives its sidebar every tab the app registers in one expanded group, lays it out at a size where that list must
/// scroll, and compares each name's laid-out width with the width its text needs in the font and weight the row
/// actually resolved.
/// <para>Scrolling is part of the case on purpose, and it is also where a harness outside the window got it wrong. A
/// plain WPF ScrollViewer puts its 17 px bar beside the content and takes that width from every row. The app's
/// scrollers are styled by WPF-UI's <c>ControlsDictionary</c>, which draws a thin bar OVER the rows' empty right
/// edge, so the names keep their 155 px. Measured here, in the real window, that is what holds; a restyle that put
/// the bar back beside the names would trim the widest of them and fail this test.</para>
/// <para>The names and their PREVIEW flags are read from <c>MainWindowViewModel.cs</c> rather than from a built view
/// model: building one constructs every tab's view model, which is the cost a single real one already puts on this
/// suite. The pattern is the one <c>ArchitectureTests.NavEntry</c> uses, and the floors below fail if it stops
/// matching.</para>
/// </remarks>
public sealed class SidebarRowFitTests
{
    /// <summary>What the sidebar binds to, and nothing else, so no tab's view model is built.</summary>
    private sealed class SidebarSurface
    {
        public ObservableCollection<NavGroup> NavGroups { get; } = [];
        public ObservableCollection<NavItem> NavResults { get; } = [];
        public bool IsSearchingNav => false;
    }

    // Tab<TVm>("nav-id", "Label", typeof(Views.SomeView), …)  /  EagerItem(…). Same shape as ArchitectureTests.NavEntry.
    private static readonly Regex NavEntry = new(
        @"(?:Tab<\w+>|EagerItem)\(\s*""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*typeof\(Views\.(\w+)\)",
        RegexOptions.Compiled);

    private static List<(string Id, string Label, bool Preview)> RegisteredTabs()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppProject(), "ViewModels", "MainWindowViewModel.cs"));
        var entries = NavEntry.Matches(source).Cast<Match>().ToArray();
        var tabs = new List<(string, string, bool)>();
        for (var i = 0; i < entries.Length; i++)
        {
            // The flag is a trailing argument, so it lives between this entry and the next one.
            var from = entries[i].Index + entries[i].Length;
            var to = i + 1 < entries.Length ? entries[i + 1].Index : source.Length;
            var preview = source[from..to].Contains("inDevelopment: true", StringComparison.Ordinal);
            tabs.Add((entries[i].Groups[1].Value, entries[i].Groups[2].Value, preview));
        }
        return tabs;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Lays the window's content out at a size where the full list of tabs cannot fit.</summary>
    private static void Lay(FrameworkElement root)
    {
        root.Measure(new Size(1280, 640));
        root.Arrange(new Rect(0, 0, 1280, 640));
        root.UpdateLayout();
    }

    private static double TextWidth(TextBlock block) => new FormattedText(
            block.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, Brushes.Black, pixelsPerDip: 1.0)
        .WidthIncludingTrailingWhitespace;

    [Fact]
    public void EverySidebarName_FitsItsRow_EvenWhenTheTreeScrolls()
    {
        var tabs = RegisteredTabs();
        Assert.True(tabs.Count >= 50,
            $"only {tabs.Count} tabs were read from MainWindowViewModel.cs; the registration pattern changed, so "
            + "this test would measure almost nothing.");
        var previews = tabs.Count(t => t.Preview);
        Assert.True(previews >= 1,
            "no tab was read as PREVIEW, so the rows this exists for are not in the measurement. The inDevelopment "
            + "detection is out of date.");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var surface = new SidebarSurface();
            var all = new NavGroup { Id = "grp-all", Label = "Every tab", Glyph = "\uE80F", IsExpanded = true };
            foreach (var (id, label, preview) in tabs)
            {
                all.Children.Add(new NavItem
                {
                    Id = id,
                    Label = label,
                    Content = new object(),
                    ViewType = typeof(object),
                    IsInDevelopment = preview,
                });
            }
            surface.NavGroups.Add(all);

            var window = new MainWindow { DataContext = surface };
            try
            {
                // A short window, so the whole list cannot fit and the tree scrolls, with its bar showing.
                var root = (FrameworkElement)window.Content;
                Lay(root);

                // A group opens by animating its content's ScaleY from 0 to 1, and an animation never advances in a
                // window that is not on screen, so the content would stay 0 px tall. Put each group where its
                // animation ends, then lay out again.
                foreach (var expander in Descendants(root).OfType<Expander>())
                {
                    if (expander.Template.FindName("ContentPanel", expander) is FrameworkElement panel)
                    {
                        panel.LayoutTransform = Transform.Identity;
                        panel.RenderTransform = Transform.Identity;
                    }
                }
                Lay(root);

                // Visibility, not IsVisible: IsVisible is false for everything in a window that was never shown.
                var tree = Descendants(root).OfType<ScrollViewer>()
                    .Single(s => s.Visibility == Visibility.Visible && Descendants(s).OfType<FrameworkElement>()
                        .Any(e => e.DataContext is NavGroup));

                var names = Descendants(tree).OfType<TextBlock>()
                    .Where(t => t.DataContext is NavItem item && t.Text == item.Label)
                    .ToList();
                Assert.True(names.Count == tabs.Count,
                    $"{names.Count} names were laid out for {tabs.Count} tabs; the tree did not draw every row, so the "
                    + "measurement below is incomplete.");
                Assert.True(tree.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                    $"the tree did not scroll (extent {tree.ExtentHeight:F0} px, viewport {tree.ViewportHeight:F0} px), "
                    + "so the names were not measured with the scrollbar showing.");

                var trimmed = names
                    .Select(n => (Name: n.Text, Room: n.ActualWidth, Needs: TextWidth(n)))
                    .Where(n => n.Room + 0.5 < n.Needs)
                    .Select(n => $"\"{n.Name}\" needs {n.Needs:F1} px and its row gives it {n.Room:F1}")
                    .ToList();
                Assert.True(trimmed.Count == 0,
                    "these sidebar names are cut short with an ellipsis:\n  " + string.Join("\n  ", trimmed));

                // And every PREVIEW tab still says so, on its own line.
                var marks = Descendants(tree).OfType<TextBlock>()
                    .Count(t => t.Text == "Preview" && t.Visibility == Visibility.Visible
                                && t.DataContext is NavItem { IsInDevelopment: true });
                Assert.Equal(previews, marks);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
