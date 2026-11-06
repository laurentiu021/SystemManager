// SysManager · MenuHeaderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SysManager.IntegrationTests;

/// <summary>
/// A menu item's header made from a name shows the name exactly and is announced exactly (#3199).
/// </summary>
/// <remarks>
/// WPF-UI's menu item template draws its header with <c>RecognizesAccessKey</c>, so the header goes through an
/// <c>AccessText</c>, which reads the first underscore as an access key: it does not draw it and drops it from the
/// item's automation name. The row menus build their headers from names, so for a program named <c>my_app.exe</c>
/// they read and were announced "Kill myapp.exe". This lays out a menu item in the app's resources with its header made
/// by the converter the row menus use, and compares its automation name with the words and, for a name with an
/// underscore, what it draws with the same words in a plain TextBlock.
/// </remarks>
public sealed class MenuHeaderTests
{
    [Theory]
    [InlineData("my_app.exe")]
    [InlineData("a__b.exe")]
    [InlineData("_start_up_.exe")]
    [InlineData("notepad.exe")]
    public void AHeaderFromAName_ShowsAndAnnouncesTheName(string name)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            foreach (var style in new Style?[] { null, (Style)Application.Current.Resources["DangerMenuItem"] })
            {
                var converter = (IValueConverter)Application.Current.Resources["MenuHeader"];
                var item = new MenuItem
                {
                    Header = converter.Convert(name, typeof(object), "Kill {0}", CultureInfo.InvariantCulture),
                };
                if (style is not null) item.Style = style;
                var menu = new ContextMenu();
                menu.Items.Add(item);
                menu.Measure(new Size(800, 200));
                menu.Arrange(new Rect(0, 0, 800, 200));
                menu.UpdateLayout();

                var words = $"Kill {name}";
                var shape = style is null ? "a menu item" : "a danger menu item";
                var announced = UIElementAutomationPeer.CreatePeerForElement(item).GetName();
                Assert.True(announced == words, $"{shape} headed \"{words}\" is announced as \"{announced}\".");

                // What is drawn is compared for a name with an underscore, the thing an AccessText changes. A header
                // with none is a plain TextBlock, and that path is not what this tests.
                if (!name.Contains('_', StringComparison.Ordinal)) continue;
                var drawn = Descendants<TextBlock>(item).Single(t => new TextRange(t.ContentStart, t.ContentEnd).Text
                    .Contains("Kill", StringComparison.Ordinal));
                var plain = new TextBlock
                {
                    Text = words,
                    FontFamily = drawn.FontFamily,
                    FontSize = drawn.FontSize,
                    FontWeight = drawn.FontWeight,
                    FontStyle = drawn.FontStyle,
                };
                plain.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(Math.Abs(drawn.DesiredSize.Width - plain.DesiredSize.Width) < 0.01,
                    $"{shape} headed \"{words}\" draws {drawn.DesiredSize.Width:F1} px wide, where the words are "
                    + $"{plain.DesiredSize.Width:F1}, so it does not draw them as they are.");
            }
        });
    }

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
