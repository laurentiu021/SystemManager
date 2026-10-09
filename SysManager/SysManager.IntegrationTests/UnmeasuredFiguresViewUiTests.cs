// SysManager · UnmeasuredFiguresViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// System Health's disk card and Ping's target rows really show "—" for a figure that was not measured (#2600).
/// </summary>
/// <remarks>
/// The unit tests prove the models format it; this proves the views show it, the way <see cref="SpeedTestViewUiTests"/>
/// does for Speed Test. The bindings formatted the raw numbers, and a binding's fallback never stands in for a null
/// value: LIFE REMAINING read 100% for a drive that reports no wear, its temperature read " °C" and its error counts
/// nothing, and a ping that timed out read " ms". Each view gets only the list it shows as its data context, so the
/// rest of the view binds to nothing and is not read.
/// </remarks>
public sealed class UnmeasuredFiguresViewUiTests
{
    private sealed class DiskCards
    {
        public ObservableCollection<DiskHealthReport> DiskHealth { get; } = [];
    }

    private sealed class PingRows
    {
        public TargetList Shared { get; } = new();
    }

    private sealed class TargetList
    {
        public ObservableCollection<PingTarget> Targets { get; } = [];
    }

    [Fact]
    public void TheDiskCard_ShowsADash_ForEveryFigureTheDriveDoesNotReport()
    {
        var context = new DiskCards();
        context.DiskHealth.Add(new DiskHealthReport { FriendlyName = "Nothing reported", HealthStatus = "SomethingUnknown" });

        StaHelper.Run(() =>
        {
            var texts = TextsOf(Laid(new SystemHealthView(), context));

            Assert.Equal("—", After(texts, "HEALTH"));
            Assert.Equal("—", After(texts, "TEMPERATURE"));
            Assert.Equal("—", After(texts, "LIFE REMAINING"));
            Assert.Equal("—", After(texts, "READ ERRORS"));
            Assert.Equal("—", After(texts, "WRITE ERRORS"));
        });
    }

    [Fact]
    public void TheDiskCard_ShowsWhatTheDriveReports()
    {
        var context = new DiskCards();
        context.DiskHealth.Add(new DiskHealthReport
        {
            FriendlyName = "Everything reported",
            HealthStatus = "Healthy",
            TemperatureC = 38,
            WearPercent = 4,
            ReadErrors = 0,
            WriteErrors = 2,
        });

        StaHelper.Run(() =>
        {
            var texts = TextsOf(Laid(new SystemHealthView(), context));

            Assert.EndsWith("%", After(texts, "HEALTH"), StringComparison.Ordinal);
            Assert.Equal("38 °C", After(texts, "TEMPERATURE"));
            Assert.Equal("96%", After(texts, "LIFE REMAINING"));
            Assert.Equal("0", After(texts, "READ ERRORS"));
            Assert.Equal("2", After(texts, "WRITE ERRORS"));
        });
    }

    [Fact]
    public void APingRow_ShowsADash_ForWhatWasNotMeasured()
    {
        var context = new PingRows();
        context.Shared.Targets.Add(new PingTarget("Router", "192.168.1.1", "#4CC9F0"));

        StaHelper.Run(() =>
        {
            var texts = TextsOf(Laid(new PingView(), context));

            // The value sits above its label in a ping row.
            Assert.Equal("—", Before(texts, "LATENCY"));
            Assert.Equal("—", Before(texts, "AVG"));
            Assert.Equal("—", Before(texts, "JITTER"));
        });
    }

    private static FrameworkElement Laid(FrameworkElement view, object context)
    {
        AppResources.Ensure();
        view.DataContext = context;
        view.Measure(new Size(1400, 1600));
        view.Arrange(new Rect(0, 0, 1400, 1600));
        view.UpdateLayout();
        return view;
    }

    private static List<string> TextsOf(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is TextBlock block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static string After(List<string> texts, string label)
    {
        var at = texts.IndexOf(label);
        Assert.True(at >= 0 && at + 1 < texts.Count, $"no \"{label}\" figure: {string.Join(" | ", texts)}");
        return texts[at + 1];
    }

    private static string Before(List<string> texts, string label)
    {
        var at = texts.IndexOf(label);
        Assert.True(at >= 1, $"no \"{label}\" figure: {string.Join(" | ", texts)}");
        return texts[at - 1];
    }
}
