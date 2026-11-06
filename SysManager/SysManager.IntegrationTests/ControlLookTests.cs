// SysManager · ControlLookTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The controls drawn by Windows' templates take the app's look instead (#2816).
/// </summary>
/// <remarks>
/// A style that sets neither a template nor <c>BasedOn</c> leaves the control's template to Windows' theme, not to the
/// WPF-UI dictionaries <c>App.xaml</c> merges. <c>ArchitectureTests.NoStyle_LeavesAButtonOrListItemToWindowsTemplate</c>
/// reads that from the markup; this lays a control out with the app's resources and reads the template it got.
/// </remarks>
public sealed class ControlLookTests
{
    /// <summary>
    /// Performance Mode's power plans are the app's radio button: an accent dot in a ring, not Windows' white circle.
    /// </summary>
    [Fact]
    public void ThePowerPlans_AreTheAppsRadioButton()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new PerformanceView();
            view.Measure(new Size(PageWidth.Default, 900));
            view.Arrange(new Rect(0, 0, PageWidth.Default, 900));
            view.UpdateLayout();

            // The power plans are the radio buttons with no style of their own, so they take App.xaml's.
            var plans = Descendants<RadioButton>(view)
                .Where(r => r.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
                .ToList();
            Assert.True(plans.Count == 3,
                $"{plans.Count} power-plan radio buttons were laid out, where there are three.");
            foreach (var plan in plans)
            {
                Assert.IsType<Ellipse>(plan.Template.FindName("Dot", plan));
                Assert.IsType<Ellipse>(plan.Template.FindName("Ring", plan));
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
