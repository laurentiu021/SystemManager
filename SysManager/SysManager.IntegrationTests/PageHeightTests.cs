// SysManager · PageHeightTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every page that stretches a row is a <see cref="PageGrid"/>, and in the shell's scroll viewer, at the default window
/// and at the smallest, cuts nothing it shows (#2704).
/// </summary>
/// <remarks>
/// Pages were fixed rows over a list, a console or a chart that took what was left, with no scrolling, so a short
/// window gave that row nothing and cut what was under it: System Fixes' console had 0 px at the default window with
/// 186 px of the page past its edge, and at the smallest window fifteen pages left such a row under 160 px. This builds
/// every view with the app's resources and no data, hosts it as the shell does, in a <see cref="PageFit"/> inside a
/// <see cref="ScrollViewer"/>, and lays it out at the page each window gives a tab. It fails on a page whose root grid
/// stretches a row but is not a <see cref="PageGrid"/>, and on anything cut vertically from the shell's host down: a
/// page laid out shorter than its rows shows as its host cut, and a stretching row whose floor is too low for what it
/// holds as that content cut. It looks inside neither lists nor text boxes, whose scrolling cuts what they hold on
/// purpose.
/// <para>A view is built with no data, so what its bindings would hide shows: a chart and the empty state that takes
/// its place, both in one cell. A floor that holds both holds each.</para>
/// </remarks>
public sealed class PageHeightTests
{
    [Theory]
    [InlineData(PageWidth.Default, 781)]
    [InlineData(PageWidth.Smallest, 601)]
    public void EveryPageThatStretchesARow_IsAPageGrid_AndCutsNothingInTheShell(int width, int height)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var views = AppViews.Constructible();
            Assert.True(views.Count >= 60, $"only {views.Count} views were found, where there are 67.");

            var problems = new List<string>();
            var pages = 0;
            var read = 0;
            foreach (var type in views)
            {
                var view = (UserControl)Activator.CreateInstance(type)!;
                if (view.Content is not Grid root || !root.RowDefinitions.Any(r => r.Height.IsStar)) continue;
                if (root is not PageGrid page)
                {
                    problems.Add($"{type.Name}: its root grid stretches a row but is a Grid, not a PageGrid");
                    continue;
                }

                pages++;
                var fit = new PageFit { Child = new ContentControl { Content = view } };
                var host = new Grid { Width = width, Height = height };
                host.Children.Add(new ScrollViewer
                {
                    CanContentScroll = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = fit,
                });
                host.Measure(new Size(width, height));
                host.Arrange(new Rect(0, 0, width, height));
                host.UpdateLayout();

                foreach (var element in LayoutClip.LaidOut(fit, e => e is not ScrollViewer and not TextBoxBase
                             and not DataGrid and not ItemsControl))
                {
                    read++;
                    if (!LayoutClip.IsCut(element, out var room) || room.Height + 1 >= element.ActualHeight) continue;
                    problems.Add($"{type.Name}: {LayoutClip.Describe(element)} is {element.ActualHeight:F0} px tall, "
                        + $"shown {LayoutClip.Shown(room)}{RowOf(page, element)}");
                }
            }

            // Vacuity floors, both measured: 53 pages stretch a row, and 3,990 elements were read across them.
            Assert.True(pages >= 50, $"only {pages} pages were laid out, where 53 stretch a row.");
            Assert.True(read >= 3500,
                $"only {read} elements were read across {pages} pages, out of 3,990 measured, so a pass means little.");
            Assert.True(problems.Count == 0, $"at a page {width} x {height}:\n  " + string.Join("\n  ", problems));
        });
    }

    /// <summary>
    /// Which of the page's rows <paramref name="element"/> is in, and that row's height, for a failure message.
    /// </summary>
    private static string RowOf(PageGrid page, DependencyObject element)
    {
        if (element == page || !page.IsAncestorOf(element)) return "";
        var node = element;
        while (VisualTreeHelper.GetParent(node) is { } parent && parent != page) node = parent;
        if (node is not UIElement child) return "";
        var row = Grid.GetRow(child);
        return $", in row {row}, {page.RowDefinitions[row].ActualHeight:F0} px";
    }
}
