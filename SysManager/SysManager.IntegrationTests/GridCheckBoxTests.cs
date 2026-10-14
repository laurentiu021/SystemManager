// SysManager · GridCheckBoxTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every checkbox in a grid is the app's checkbox, with the focus ring, and fits the room its column leaves (#2696).
/// </summary>
/// <remarks>
/// A checkbox column gives the checkbox it draws a style of its own, which replaces the app's implicit CheckBox style
/// instead of adding to it. Laid out off screen with App.xaml's resources, the six row-picking columns drew WPF's
/// default checkbox, with the default focus rectangle, and the three checkboxes placed in grids by hand kept the app's
/// template but its 8 px label padding, which cut the box: Browser Cleaner's and Debloater's by 2 px, and Windows
/// Update's select-all checkbox in its column header down to 12 px of its 18. Each takes <c>GridCheckBox</c> now,
/// and the two columns too narrow for the box are wider. A cell keeps 10 px on each side and a column header 12.
/// <para>Every view is built, and every grid is given one placeholder row so its checkbox column draws a checkbox.
/// <c>ArchitectureTests.EveryCheckBoxInAGrid_IsTheAppsCheckBox</c> holds the same from source.</para>
/// </remarks>
public class GridCheckBoxTests
{
    [Fact]
    public void EveryCheckBoxInAGrid_IsTheAppsCheckBox_AndFits()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var app = Application.Current!;
            var appTemplate = ((Style)app.FindResource(typeof(CheckBox))).Setters.OfType<Setter>()
                .First(s => s.Property == Control.TemplateProperty).Value;
            var ring = app.FindResource("FocusRing");

            var views = typeof(App).Assembly.GetTypes()
                .Where(t => t.Namespace == "SysManager.Views" && typeof(UserControl).IsAssignableFrom(t)
                            && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            var offenders = new List<string>();
            var found = 0;
            foreach (var type in views)
            {
                var view = (UserControl)Activator.CreateInstance(type)!;
                var host = new Grid { Width = 1280, Height = 900 };
                host.Children.Add(view);
                Lay(host);
                var grids = Descendants<DataGrid>(view).ToList();
                if (grids.Count == 0) continue;
                foreach (var grid in grids) grid.ItemsSource = new object[] { new() };
                Lay(host);

                foreach (var box in grids.SelectMany(Descendants<CheckBox>))
                {
                    found++;
                    var holder = Ancestors(box).OfType<Control>()
                        .FirstOrDefault(a => a is DataGridCell or DataGridColumnHeader);
                    var where = $"{type.Name}, a checkbox in a {(holder is DataGridColumnHeader ? "column header" : "cell")}";
                    if (!ReferenceEquals(box.Template, appTemplate))
                        offenders.Add($"{where}: draws WPF's default checkbox, not the app's");
                    if (!ReferenceEquals(box.FocusVisualStyle, ring))
                        offenders.Add($"{where}: shows the default focus rectangle, not the focus ring");
                    if (holder is not null && box.Template?.FindName("Box", box) is FrameworkElement part)
                    {
                        var room = holder.ActualWidth - holder.Padding.Left - holder.Padding.Right;
                        var needs = part.Width + box.Padding.Left + box.Padding.Right;
                        if (needs > room + 0.5)
                            offenders.Add($"{where}: needs {needs:F0} px and its column leaves {room:F0}, so the box is cut");
                    }
                }
            }

            // Vacuity floor: nine checkboxes in grids, measured for #2696: six checkbox columns, two template columns
            // and Windows Update's select-all header.
            Assert.True(found >= 9, $"only {found} checkboxes were found in grids, out of 9 measured.");
            Assert.True(offenders.Count == 0,
                "these checkboxes in a grid are not the app's checkbox, or do not fit:\n  " + string.Join("\n  ", offenders));
        });
    }

    private static void Lay(FrameworkElement host)
    {
        host.Measure(new Size(1280, 900));
        host.Arrange(new Rect(0, 0, 1280, 900));
        host.UpdateLayout();
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }
}
