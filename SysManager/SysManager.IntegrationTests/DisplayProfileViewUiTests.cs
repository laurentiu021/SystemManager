// SysManager · DisplayProfileViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Display Profiles' Keep, built from the real XAML with the app's dictionaries, takes focus when its bar appears
/// (#2883).
/// </summary>
/// <remarks>
/// The unit test reads the attribute out of the file. This loads the view, so an attached property that no longer
/// resolves, or a Keep that lost it, fails here rather than when the bar first appears on someone's screen.
/// </remarks>
public sealed class DisplayProfileViewUiTests
{
    [Fact]
    public void Keep_TakesFocusWhenTheBarAppears()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new DisplayProfileView();
            view.Measure(new Size(1044, 900));
            view.Arrange(new Rect(0, 0, 1044, 900));
            view.UpdateLayout();

            var keep = Assert.Single(Descendants<Button>(view),
                b => AutomationProperties.GetName(b) == "Keep the new display settings");
            Assert.True(FocusWhenShown.GetTake(keep));
            Assert.All(Descendants<Button>(view).Where(b => !ReferenceEquals(b, keep)),
                b => Assert.False(FocusWhenShown.GetTake(b)));
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
