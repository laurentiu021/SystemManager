// SysManager · ListCellTextTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

/// <summary>
/// A list cell's value too long for its column ends in "…" and shows whole on hover while it is cut (#3214).
/// </summary>
/// <remarks>
/// <c>ArchitectureTests.EveryListTextColumn_DrawsItsCellsInTheSharedCellStyle</c> reads the markup. This builds every
/// view with the app's resources and checks that the list style really gave each text column <c>GridCellText</c>,
/// then lays out a list with a cut value and one that fits, and reads what <see cref="CutText"/> decides for each.
/// </remarks>
public sealed class ListCellTextTests
{
    private sealed record Row(string Name);

    [Fact]
    public void EveryTextColumnInTheViews_DrawsItsCellsInGridCellText()
    {
        var wrong = new List<string>();
        var grids = 0;
        var columns = 0;
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var cellStyle = (Style)Application.Current.FindResource("GridCellText");
            foreach (var type in AppViews.Constructible())
            {
                var view = (UserControl)Activator.CreateInstance(type)!;
                var host = new Grid { Width = PageWidth.Default, Height = 900 };
                host.Children.Add(view);
                host.Measure(new Size(PageWidth.Default, 900));
                host.Arrange(new Rect(0, 0, PageWidth.Default, 900));
                host.UpdateLayout();

                foreach (var grid in Descendants<DataGrid>(view).Union(LogicalDescendants<DataGrid>(view)))
                {
                    grids++;
                    if (!ReferenceEquals(GridText.GetCellStyle(grid), cellStyle))
                        wrong.Add($"{type.Name}: a list the list style does not give GridCellText");
                    foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
                    {
                        columns++;
                        if (!BuildsOn(column.ElementStyle, cellStyle))
                            wrong.Add($"{type.Name}: the \"{column.Header}\" column draws its cells in a style not "
                                      + "built on GridCellText");
                    }
                }
            }
        });

        // Vacuity floors: 35 lists and 128 text columns in the views when written.
        Assert.True(grids >= 35, $"only {grids} lists were found in the views, out of 35 when written.");
        Assert.True(columns >= 128, $"only {columns} text columns were read, out of 128 when written.");
        Assert.True(wrong.Count == 0,
            "these lists cut a long value at the column's edge:\n  " + string.Join("\n  ", wrong));
    }

    [Fact]
    public void AValueTooLongForItsColumn_EndsInAnEllipsis_AndShowsWholeOnHover()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                ItemsSource = new[] { new Row("speed.cloudflare.com"), new Row("Bucharest") },
            };
            var host = new Grid { Width = 300, Height = 200 };
            host.Children.Add(grid);
            Lay(host);
            // Added once the list is laid out, as a column a view adds later would be.
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Server",
                Binding = new Binding(nameof(Row.Name)),
                Width = 80,
            });
            Lay(host);

            var cut = CellText(grid, "speed.cloudflare.com");
            Assert.Equal(TextTrimming.CharacterEllipsis, cut.TextTrimming);
            Assert.Equal("speed.cloudflare.com", cut.ToolTip);
            Assert.True(CutText.IsCut(cut), $"\"{cut.Text}\" in an 80 px column read as whole");
            Assert.True(CutText.ShouldShow(cut), "the tooltip of a cut value would not open");

            var whole = CellText(grid, "Bucharest");
            Assert.False(CutText.IsCut(whole), $"\"{whole.Text}\" in an 80 px column read as cut");
            Assert.False(CutText.ShouldShow(whole), "the tooltip would open over a value that is already whole");
        });
    }

    [Fact]
    public void ATooltipThatExplainsTheValue_OpensWhetherOrNotTheValueIsCut()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var explained = new TextBlock { Text = "On", ToolTip = "Blocked by the privacy preset" };
            var empty = new TextBlock { Text = "", ToolTip = "" };
            var host = new StackPanel { Width = 300 };
            host.Children.Add(explained);
            host.Children.Add(empty);
            Lay(host);

            Assert.False(CutText.IsCut(explained));
            Assert.True(CutText.ShouldShow(explained), "an explanation in the tooltip was kept from a value that fits");
            Assert.False(CutText.IsCut(empty), "an empty text read as cut");
            Assert.False(CutText.ShouldShow(empty), "an empty tooltip would open over an empty cell");
        });
    }

    private static void Lay(FrameworkElement host)
    {
        host.Measure(new Size(host.Width, double.IsNaN(host.Height) ? 400 : host.Height));
        host.Arrange(new Rect(0, 0, host.Width, double.IsNaN(host.Height) ? 400 : host.Height));
        host.UpdateLayout();
    }

    private static TextBlock CellText(DataGrid grid, string text) =>
        Assert.Single(Descendants<DataGridCell>(grid).SelectMany(Descendants<TextBlock>), t => t.Text == text);

    private static bool BuildsOn(Style? style, Style root)
    {
        for (var s = style; s is not null; s = s.BasedOn)
            if (ReferenceEquals(s, root)) return true;
        return false;
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

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in LogicalDescendants<T>(child)) yield return deeper;
        }
    }
}
