// SysManager · BootAnalyzerViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every kind Boot Analyzer can name fits the Kind column of its "Slowed boot" list, laid out with the app's styles
/// (#2770).
/// </summary>
/// <remarks>
/// The kinds are read from <see cref="BootAnalyzerService.KindForEventId"/> for every ID from 100 to 111, so the ten
/// Windows writes and the one used for an ID it does not know are all measured, and a kind added or renamed there is
/// measured the day it changes. The column has no ellipsis, so a kind wider than its room is cut off mid-word with
/// nothing to say so. A cut-off text block is still laid out at its full width and then clipped, so its own
/// ActualWidth cannot tell; the room is what the column gives, less the cell's padding and the text's margin, all read
/// from the laid-out cell.
/// </remarks>
public sealed class BootAnalyzerViewUiTests
{
    /// <summary>What the two lists bind to, and nothing else, so no view model or event log is involved.</summary>
    private sealed class BootListsSurface
    {
        public ObservableCollection<BootRecord> Boots { get; } = [];
        public ObservableCollection<BootDegradation> Degradations { get; } = [];
    }

    [Fact]
    public void EveryKind_FitsTheKindColumn()
    {
        var kinds = Enumerable.Range(100, 12).Select(BootAnalyzerService.KindForEventId).Distinct().ToList();
        Assert.True(kinds.Count >= 8 && kinds.Contains("Application") && kinds.Contains("Component"),
            $"the kinds read were {string.Join(", ", kinds)}; KindForEventId no longer gives what this measures.");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var surface = new BootListsSurface();
            var when = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Local);
            foreach (var kind in kinds) surface.Degradations.Add(new BootDegradation(when, kind, "component", 1000));

            // The page's width beside the sidebar in the window's default 1280 px.
            var view = new BootAnalyzerView { DataContext = surface };
            view.Measure(new Size(1044, 900));
            view.Arrange(new Rect(0, 0, 1044, 900));
            view.UpdateLayout();

            var grid = Descendants<DataGrid>(view)
                .Single(g => AutomationProperties.GetName(g) == "Components that slowed boot");
            var cells = Descendants<DataGridCell>(grid).Where(c => Equals(c.Column.Header, "Kind")).ToList();
            Assert.True(cells.Count == kinds.Count,
                $"{cells.Count} Kind cells were laid out for {kinds.Count} kinds, so the measurement is incomplete.");

            var cut = cells
                .Select(cell =>
                {
                    var block = Descendants<TextBlock>(cell).Single();
                    var room = cell.Column.ActualWidth - cell.Padding.Left - cell.Padding.Right
                               - block.Margin.Left - block.Margin.Right;
                    return (block.Text, Room: room, Needs: TextWidth(block));
                })
                .Where(k => k.Room + 0.5 < k.Needs)
                .Select(k => $"\"{k.Text}\" needs {k.Needs:F1} px and the column gives it {k.Room:F1}")
                .ToList();
            Assert.True(cut.Count == 0, "these kinds are cut off in the Kind column:\n  " + string.Join("\n  ", cut));
        });
    }

    private static double TextWidth(TextBlock block) => new FormattedText(
            block.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, Brushes.Black, pixelsPerDip: 1.0)
        .WidthIncludingTrailingWhitespace;

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
