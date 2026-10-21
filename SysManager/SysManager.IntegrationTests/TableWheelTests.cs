// SysManager · TableWheelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

/// <summary>
/// A turn of the mouse wheel over a table that cannot use it scrolls the page around the table (#2817).
/// </summary>
/// <remarks>
/// A DataGrid's own scroll viewer took every turn, so the page stopped while the pointer was over a table: on System
/// Health's short tables, and on Speed Test's and Drivers' once they reached either end. These lay out a table in a
/// scrolling page with the app's resources, whose DataGrid style sets <see cref="WheelPassThrough"/>, and turn the
/// wheel over a cell the way WPF does, the preview down the tree and then the bubbling event with the same arguments.
/// </remarks>
[Collection("Network")]
public sealed class TableWheelTests
{
    private const int OneNotchDown = -120;
    private const int OneNotchUp = 120;

    private sealed record Row(string Name);

    [Fact]
    public void AShortTable_PassesTheWheelToThePage()
    {
        StaHelper.Run(() =>
        {
            var (page, grid) = Page(rows: 2);
            Assert.True(WheelPassThrough.GetToPage(grid), "the app's DataGrid style did not reach the table");

            Turn(CellIn(grid), OneNotchDown);
            page.UpdateLayout();

            Assert.True(page.VerticalOffset > 0, "the page did not move under a table with nothing to scroll");
        });
    }

    [Fact]
    public void ATableThatCanStillScroll_KeepsTheWheel()
    {
        StaHelper.Run(() =>
        {
            var (page, grid) = Page(rows: 60, gridHeight: 150);

            Turn(CellIn(grid), OneNotchDown);
            page.UpdateLayout();

            Assert.Equal(0, page.VerticalOffset);
            Assert.True(ViewerOf(grid).VerticalOffset > 0, "the table did not scroll");
        });
    }

    [Fact]
    public void ATableAtEitherEnd_PassesTheWheelToThePage()
    {
        StaHelper.Run(() =>
        {
            var (page, grid) = Page(rows: 60, gridHeight: 150);
            var table = ViewerOf(grid);
            table.ScrollToEnd();
            page.UpdateLayout();

            Turn(CellIn(grid), OneNotchDown);
            page.UpdateLayout();
            var moved = page.VerticalOffset;
            Assert.True(moved > 0, "the page did not move under a table at its last row");

            table.ScrollToHome();
            page.UpdateLayout();
            Turn(CellIn(grid), OneNotchUp);
            page.UpdateLayout();
            Assert.True(page.VerticalOffset < moved, "the page did not move back under a table at its first row");
        });
    }

    [Fact]
    public void WithoutIt_TheTableTakesTheTurn()
    {
        // What #2817 described, so the page moving above is the behaviour's doing and not the way these turn the wheel.
        StaHelper.Run(() =>
        {
            var (page, grid) = Page(rows: 2);
            WheelPassThrough.SetToPage(grid, false);

            Turn(CellIn(grid), OneNotchDown);
            page.UpdateLayout();

            Assert.Equal(0, page.VerticalOffset);
        });
    }

    /// <summary>A page that scrolls, with a table of <paramref name="rows"/> rows at its top and more below.</summary>
    private static (ScrollViewer Page, DataGrid Grid) Page(int rows, double gridHeight = double.NaN)
    {
        AppResources.Ensure();
        var grid = new DataGrid
        {
            ItemsSource = Enumerable.Range(1, rows).Select(i => new Row($"Row {i}")).ToList(),
            AutoGenerateColumns = false,
            Height = gridHeight,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) });

        var content = new StackPanel();
        content.Children.Add(grid);
        content.Children.Add(new Border { Height = 2000 });
        var page = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        page.Measure(new Size(600, 400));
        page.Arrange(new Rect(0, 0, 600, 400));
        page.UpdateLayout();
        return (page, grid);
    }

    /// <summary>
    /// Turns the wheel over <paramref name="target"/> as WPF does: the preview from the top of the tree down to it,
    /// then the bubbling event from it up, with the same arguments, so a preview marked handled stays handled.
    /// </summary>
    private static void Turn(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        target.RaiseEvent(args);
        args.RoutedEvent = UIElement.MouseWheelEvent;
        target.RaiseEvent(args);
    }

    private static DataGridCell CellIn(DataGrid grid) => Descendants(grid).OfType<DataGridCell>().First();

    private static ScrollViewer ViewerOf(DataGrid grid) => Descendants(grid).OfType<ScrollViewer>().First();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
