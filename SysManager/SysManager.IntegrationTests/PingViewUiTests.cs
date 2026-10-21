// SysManager · PingViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Ping chart's window picker really reads in minutes and still sets the window in seconds (#2884).
/// </summary>
/// <remarks>
/// The unit test proves the options' labels. It cannot prove the picker shows them: the app's ComboBox template does
/// not pass an item format to the closed box, and a picker whose value path is misspelt compiles and sets nothing. So
/// this loads the real XAML on the STA thread with the app's dictionaries, as <see cref="SpeedTestViewUiTests"/> does.
/// Nothing is pinged: the monitor is never started.
/// </remarks>
[Collection("Network")]
public sealed class PingViewUiTests
{
    [Fact]
    public void TheWindowPicker_ReadsInMinutes_AndSetsTheWindowInSeconds()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var shared = new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
                new TracerouteMonitorService(), new SpeedTestService(),
                new NetworkRepairService(new PowerShellRunner()));
            var view = new PingView { DataContext = new PingViewModel(shared) };
            Relayout(view);

            var picker = Assert.Single(Descendants<ComboBox>(view),
                c => AutomationProperties.GetName(c) == "Chart time window in minutes");
            Assert.Equal(60, Assert.IsType<ChartWindowOption>(picker.SelectedItem).Seconds);
            Assert.Contains("1 min", TextsOf(picker));

            picker.SelectedIndex = 1;
            Relayout(view);

            Assert.Equal(300, shared.WindowSeconds);
            Assert.Contains("5 min", TextsOf(picker));
            Assert.DoesNotContain("300", TextsOf(picker));
        });
    }

    [Fact]
    public void EachTargetsCardBar_ShowsTheColourItsLinesAreDrawnIn()
    {
        // On a light theme the lines move from the designed colours to be seen on the card, and the bar on each
        // target's card, which says which line is whose, moves with them. It kept the designed colour, #80FFDB at
        // 1.03:1 on Clean Indigo's card (#2818). The hosts are documentation addresses.
        var light = ThemePreset.Defaults["clean-indigo"];

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var shared = new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
                new TracerouteMonitorService(), new SpeedTestService(),
                new NetworkRepairService(new PowerShellRunner()))
            {
                Theme = () => light,
            };
            List<PingTarget> added = [];
            for (var i = 1; i <= NetworkSharedState.Palette.Length; i++)
            {
                shared.AddTarget($"Edge {i}", $"203.0.113.{i}", isCustom: true);
                added.Add(shared.Targets[^1]);
            }

            var view = new PingView { DataContext = new PingViewModel(shared) };
            Relayout(view);

            var bars = Descendants<Border>(view).Where(b => b.Width == 4 && b.DataContext is PingTarget).ToList();
            var moved = 0;
            foreach (var target in added)
            {
                var bar = Assert.Single(bars, b => ReferenceEquals(b.DataContext, target));
                var line = shared.LatencySeries.OfType<LineSeries<DateTimePoint>>()
                    .Single(s => s.Name!.EndsWith($"({target.Host})", StringComparison.Ordinal));
                var drawn = ((SolidColorPaint)line.Stroke!).Color;

                var shown = ((SolidColorBrush)bar.Background).Color;
                Assert.Equal(Color.FromRgb(drawn.Red, drawn.Green, drawn.Blue), shown);
                if (!string.Equals(target.ShownColorHex, target.ColorHex, StringComparison.OrdinalIgnoreCase)) moved++;
            }

            // Most of the palette has to move on Clean Indigo's card; a check where none moved would prove nothing.
            Assert.True(moved >= 4, $"only {moved} of the {added.Count} colours moved on Clean Indigo");
        });
    }

    private static void Relayout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
    }

    private static List<string> TextsOf(DependencyObject root) =>
        [.. Descendants<TextBlock>(root).Select(t => t.Text).Where(t => t.Length > 0)];

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
