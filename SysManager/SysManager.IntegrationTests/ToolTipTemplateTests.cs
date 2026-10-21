// SysManager · ToolTipTemplateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// A tooltip longer than the app's tooltip width wraps onto more lines instead of being cut off (#2815).
/// </summary>
/// <remarks>
/// The app's ToolTip style caps the width at 360 and presents its content with a bare ContentPresenter, so a string
/// tooltip became one TextBlock that did not wrap: Bulk Installer's "Load app icons from the web" tooltip needed 978 px
/// on one line and showed 338 px of it, losing the part about what Google learns. This lays that tooltip out with the
/// app's resources, the way WPF does when it shows one: a ToolTip whose content is the string.
/// </remarks>
[Collection("Network")]
public sealed class ToolTipTemplateTests
{
    [Fact]
    public void ALongTooltip_WrapsInsteadOfBeingCutOff()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var text = (string)Logical(new BulkInstallerView()).OfType<FrameworkElement>()
                .Single(e => AutomationProperties.GetName(e) == "Load app icons from the web").ToolTip;
            Assert.EndsWith("which reveals the queried app domains to Google.", text, StringComparison.Ordinal);

            // A ToolTip takes no parent but its popup, which needs a window, so it is laid out on its own, with the
            // app's ToolTip style it would take from App.xaml.
            var tip = new ToolTip { Content = text, Style = (Style)Application.Current.FindResource(typeof(ToolTip)) };
            tip.Measure(new Size(2000, 2000));
            tip.Arrange(new Rect(tip.DesiredSize));
            tip.UpdateLayout();
            var shown = Descendants(tip).OfType<TextBlock>().Single(t => t.Text == text);

            Assert.Equal(TextWrapping.Wrap, shown.TextWrapping);
            Assert.True(tip.ActualWidth <= 360, $"the tooltip is {tip.ActualWidth:F0} px wide");
            Assert.True(shown.ActualHeight > 2 * shown.FontSize * 1.2,
                $"the text is {shown.ActualHeight:F0} px tall, one line, so most of it is cut off");
        });
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

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Logical(child)) yield return deeper;
        }
    }
}
