// SysManager · SpeedTestViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Speed Test cards and history rows really show "—" for an upload or ping that was not measured.
/// </summary>
/// <remarks>
/// The unit tests prove the result formats it. They cannot prove the view shows it: the bindings used to format the
/// raw number themselves, and a ping with no answer read 0 ms, a perfect score (#2504). A binding left on the raw
/// value compiles, and so does one naming a property that does not exist. So this loads the real XAML on the STA
/// thread with the app's dictionaries and reads the rendered text back, the way <see cref="AppBlockerWarningUiTests"/>
/// does.
/// </remarks>
public sealed class SpeedTestViewUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    // The HTTP run's upload was refused; the Ookla run's ping got no answer.
    private static readonly SpeedTestResult NoUpload =
        new("HTTP", 312.4, null, 12.3, "speed.cloudflare.com", new DateTime(2026, 9, 29, 9, 0, 0));

    private static readonly SpeedTestResult NoPing =
        new("Ookla", 480.2, 95.1, null, "Bucharest", new DateTime(2026, 9, 29, 9, 5, 0));

    /// <summary>Built off the STA thread and handed to the view: it holds no thread-affine objects.</summary>
    private async Task<SpeedTestViewModel> VmShowingBothAsync()
    {
        var shared = new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
            new TracerouteMonitorService(), new SpeedTestService(), new NetworkRepairService(new PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new SpeedTestHistoryService(_dir));
        await vm.InitializationComplete;
        vm.HttpResult = NoUpload;
        vm.OoklaResult = NoPing;
        vm.AddToHistory(NoUpload);
        vm.AddToHistory(NoPing);
        return vm;
    }

    private static SpeedTestView Laid(SpeedTestViewModel vm)
    {
        AppResources.Ensure();
        var view = new SpeedTestView { DataContext = vm };
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task TheResultCards_ShowADash_ForWhatWasNotMeasured()
    {
        var vm = await VmShowingBothAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            var http = TextsOf(CardTitled(view, "HTTP speed test"));
            Assert.Equal("312.4 Mbps", After(http, "DOWNLOAD"));
            Assert.Equal("—", After(http, "UPLOAD"));
            Assert.Equal("12 ms", After(http, "PING"));

            var ookla = TextsOf(CardTitled(view, "Ookla speed test"));
            Assert.Equal("95.1 Mbps", After(ookla, "UPLOAD"));
            Assert.Equal("—", After(ookla, "PING"));
        });
    }

    [Fact]
    public async Task TheHistoryRows_ShowADash_ForWhatWasNotMeasured()
    {
        var vm = await VmShowingBothAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            var http = Assert.Single(RowsOf(GridNamed(view, "HTTP speed test history")));
            Assert.Equal("312.4", http["Down (Mbps)"]);
            Assert.Equal("—", http["Up (Mbps)"]);
            Assert.Equal("12", http["Ping (ms)"]);

            var ookla = Assert.Single(RowsOf(GridNamed(view, "Ookla speed test history")));
            Assert.Equal("95.1", ookla["Up (Mbps)"]);
            Assert.Equal("—", ookla["Ping (ms)"]);
        });
    }

    [Fact]
    public void TheHistoryColumns_StillSortByTheNumbers()
    {
        // The cells now show text, and sorting text would put 100.0 before 41.7.
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new SpeedTestView();

            foreach (var name in new[] { "HTTP speed test history", "Ookla speed test history" })
            {
                var columns = GridNamed(view, name).Columns.ToDictionary(c => (string)c.Header, c => c.SortMemberPath);
                Assert.Equal(nameof(SpeedTestResult.UploadMbps), columns["Up (Mbps)"]);
                Assert.Equal(nameof(SpeedTestResult.PingMs), columns["Ping (ms)"]);
            }
        });
    }

    [Fact]
    public async Task TheTrend_ShowsAboveAHistoryWithTwoRuns_AndOnlyOnItsOwnCard()
    {
        // Bound the way the view model exposes it: a chart per engine, hidden until there is a line to draw (#1499).
        var shared = new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
            new TracerouteMonitorService(), new SpeedTestService(), new NetworkRepairService(new PowerShellRunner()));
        var vm = new SpeedTestViewModel(shared, new SpeedTestHistoryService(_dir));
        await vm.InitializationComplete;
        vm.AddToHistory(new SpeedTestResult("Ookla", 470, 48, 9, "Bucharest", new DateTime(2026, 9, 27, 20, 0, 0)));
        vm.AddToHistory(new SpeedTestResult("Ookla", 490, 50, 9, "Bucharest", new DateTime(2026, 9, 28, 20, 0, 0)));
        vm.AddToHistory(NoUpload);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            var ookla = InnerCardOf(ChartNamed(view, "Ookla download and upload over time"));
            Assert.Equal(Visibility.Visible, ookla.Visibility);
            Assert.Contains("Usually about 480 Mbps.", ShownTexts(ookla));

            // One HTTP run: no line, and the card says why instead.
            var http = InnerCardOf(ChartNamed(view, "HTTP download and upload over time"));
            Assert.Equal(Visibility.Collapsed, http.Visibility);
            Assert.Contains("One test so far", ShownTexts(CardTitled(view, "HTTP History")));
            Assert.DoesNotContain("One test so far", ShownTexts(CardTitled(view, "Ookla History")));
        });
    }

    /// <summary>
    /// The text actually on screen under <paramref name="root"/>: a collapsed element still holds its bound text,
    /// so its subtree is skipped rather than read.
    /// </summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static LiveChartsCore.SkiaSharpView.WPF.CartesianChart ChartNamed(DependencyObject root, string automationName)
    {
        var chart = Descendants<LiveChartsCore.SkiaSharpView.WPF.CartesianChart>(root)
                        .FirstOrDefault(c => AutomationProperties.GetName(c) == automationName)
                    ?? LogicalDescendants<LiveChartsCore.SkiaSharpView.WPF.CartesianChart>(root)
                        .FirstOrDefault(c => AutomationProperties.GetName(c) == automationName);
        Assert.NotNull(chart);
        return chart;
    }

    /// <summary>The CardInner border a trend chart sits in, which is what its visibility is bound on.</summary>
    private static Border InnerCardOf(FrameworkElement chart)
    {
        var inner = (Style)chart.FindResource("CardInner");
        for (DependencyObject? node = chart; node is not null; node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
        {
            if (node is Border b && ReferenceEquals(b.Style, inner)) return b;
        }
        Assert.Fail("the chart is not inside a CardInner border");
        return null!;
    }

    /// <summary>The value shown right after <paramref name="label"/> in a card: its metric.</summary>
    private static string After(List<string> texts, string label)
    {
        var at = texts.IndexOf(label);
        Assert.True(at >= 0 && at + 1 < texts.Count, $"no \"{label}\" metric in the card: {string.Join(" | ", texts)}");
        return texts[at + 1];
    }

    /// <summary>The Card border around the section whose title is <paramref name="title"/>.</summary>
    private static Border CardTitled(FrameworkElement view, string title)
    {
        var card = (Style)view.FindResource("Card");
        var titleBlock = Descendants<TextBlock>(view).FirstOrDefault(t => t.Text == title);
        Assert.NotNull(titleBlock);

        Border? found = null;
        for (DependencyObject? node = titleBlock; node is not null && found is null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Border b && ReferenceEquals(b.Style, card))
                found = b;
        }
        Assert.NotNull(found);
        return found;
    }

    private static DataGrid GridNamed(DependencyObject root, string automationName)
    {
        var grid = Descendants<DataGrid>(root).FirstOrDefault(g => AutomationProperties.GetName(g) == automationName)
                   ?? LogicalDescendants<DataGrid>(root).FirstOrDefault(g => AutomationProperties.GetName(g) == automationName);
        Assert.NotNull(grid);
        return grid;
    }

    /// <summary>Each realised row, as the text of its cells keyed by column header.</summary>
    private static List<Dictionary<string, string>> RowsOf(DataGrid grid)
    {
        var rows = Descendants<DataGridRow>(grid).ToList();
        Assert.True(rows.Count > 0, "no row was realised, so this would compare against nothing");
        return rows.Select(row => Descendants<DataGridCell>(row).ToDictionary(
            cell => (string)cell.Column.Header,
            cell => string.Concat(Descendants<TextBlock>(cell).Select(t => t.Text)))).ToList();
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

    // The column test builds no layout, so its tree exists only logically.
    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in LogicalDescendants<T>(child)) yield return deeper;
        }
    }
}
