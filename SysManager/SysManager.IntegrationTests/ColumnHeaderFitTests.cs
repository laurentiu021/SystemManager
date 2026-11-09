// SysManager · ColumnHeaderFitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every list's column header fits its column, sorted or not (#3206).
/// </summary>
/// <remarks>
/// A header has 12 px of padding each side, and the sort arrow sat beside the words and took 14 px more, so sorting by
/// a narrow column cut its name: Traceroute's "#" vanished and DNS &amp; Hosts' "On" kept 2 px, and three headers
/// were cut before anything was sorted. The arrow sits above the words now. <see cref="ViewLayoutTests"/> does not
/// read inside a DataGrid, whose columns get their share of the width in a dispatcher pass this harness does not run,
/// so this lays out a header on its own, with the app's style, at the width each column declares: its fixed width,
/// or the <c>MinWidth</c> of a column that shares the leftover width. A column with neither is sized by the grid
/// alone and is not read.
/// </remarks>
public sealed class ColumnHeaderFitTests
{
    [Fact]
    public void EveryColumnHeader_FitsItsColumn_SortedOrNot()
    {
        var columns = DeclaredColumns();
        // Vacuity floor: 137 columns with words for a header and a declared width when written.
        Assert.True(columns.Count >= 137, $"only {columns.Count} columns were found, out of 137 when written.");

        var cut = new List<string>();
        var laidOut = 0;
        var arrowsDrawn = 0;
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            foreach (var (view, header, width) in columns)
            {
                foreach (var direction in new ListSortDirection?[]
                             { null, ListSortDirection.Ascending, ListSortDirection.Descending })
                {
                    var column = new DataGridColumnHeader { Content = header, Width = width };
                    var host = new StackPanel { Orientation = Orientation.Horizontal };
                    host.Children.Add(column);
                    column.ApplyTemplate();
                    if (direction is { } sort) Sort(column, sort);
                    host.Measure(new Size(1000, 100));
                    host.Arrange(new Rect(0, 0, 1000, 100));
                    host.UpdateLayout();

                    var words = Descendants<TextBlock>(column).Single(b => b.Text == header);
                    laidOut++;
                    var state = direction is null ? "" : $", sorted {direction}";
                    if (LayoutClip.IsCutAcross(words, out var room))
                    {
                        cut.Add($"{view}: \"{header}\" at {width:F0} px{state} shows "
                                + $"{LayoutClip.Shown(room)} of {words.ActualWidth:F0} px");
                    }
                    if (direction is null) continue;
                    var arrow = (FrameworkElement)column.Template.FindName("SortArrow", column);
                    if (arrow.ActualWidth > 0 && arrow.ActualHeight > 0) arrowsDrawn++;
                    if (ShownBounds(arrow, column).IntersectsWith(ShownBounds(words, column)))
                        cut.Add($"{view}: the sort arrow draws over \"{header}\"{state}");
                }
            }
        });

        Assert.Equal(columns.Count * 3, laidOut);
        // Every sorted header drew its arrow, so the overlap check measured something.
        Assert.Equal(columns.Count * 2, arrowsDrawn);
        Assert.True(cut.Count == 0,
            "these column headers do not fit their column. Widen the column, or its MinWidth, to the words and the "
            + "header's padding:\n  " + string.Join("\n  ", cut));
    }

    /// <summary>
    /// Every DataGrid column in the views with words for a header and a width it declares: a fixed width, or for a
    /// column that shares the leftover width, its <c>MinWidth</c>.
    /// </summary>
    private static List<(string View, string Header, double Width)> DeclaredColumns()
    {
        var columns = new List<(string, string, double)>();
        var views = Path.Combine(TestPaths.AppProject(), "Views");
        foreach (var file in Directory.GetFiles(views, "*.xaml").Order(StringComparer.Ordinal))
        {
            foreach (var column in XDocument.Load(file).Descendants()
                         .Where(e => e.Name.LocalName.StartsWith("DataGrid", StringComparison.Ordinal)
                                     && e.Name.LocalName.EndsWith("Column", StringComparison.Ordinal)))
            {
                if ((string?)column.Attribute("Header") is not { Length: > 0 } header || header.StartsWith('{'))
                    continue;
                var width = Number((string?)column.Attribute("Width")) ?? Number((string?)column.Attribute("MinWidth"));
                if (width is { } declared) columns.Add((Path.GetFileNameWithoutExtension(file), header, declared));
            }
        }
        return columns;
    }

    /// <summary>
    /// Sorts <paramref name="column"/> the way its template draws it: every setter of the template's trigger for
    /// <paramref name="direction"/>. The header's own SortDirection is read-only, kept in step with its grid column's.
    /// </summary>
    private static void Sort(DataGridColumnHeader column, ListSortDirection direction)
    {
        var trigger = column.Template.Triggers.OfType<Trigger>().Single(t =>
            t.Property == DataGridColumnHeader.SortDirectionProperty && Equals(t.Value, direction));
        foreach (var setter in trigger.Setters.OfType<Setter>())
        {
            var target = setter.TargetName is { } name
                ? (DependencyObject)column.Template.FindName(name, column)
                : column;
            target.SetValue(setter.Property, setter.Value);
        }
    }

    private static double? Number(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>
    /// Where the part of <paramref name="element"/> its parent shows is, in <paramref name="within"/>'s coordinates:
    /// words cut at the arrow still reach under it, out of sight.
    /// </summary>
    private static Rect ShownBounds(FrameworkElement element, FrameworkElement within)
    {
        var shown = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        if (LayoutInformation.GetLayoutClip(element) is { } clip) shown.Intersect(clip.Bounds);
        return shown.IsEmpty ? Rect.Empty : new Rect(element.TranslatePoint(shown.TopLeft, within), shown.Size);
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
