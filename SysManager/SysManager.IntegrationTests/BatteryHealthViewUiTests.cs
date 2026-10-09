// SysManager · BatteryHealthViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
/// The Battery Health tab's capacity card really shows each of its states (#1513).
/// </summary>
/// <remarks>
/// The unit tests prove the view model sets the verdict, the chart and the messages. They cannot prove the view shows
/// them: a mistyped binding path compiles and leaves a card that is always empty, or always shows everything at once.
/// So this loads the real XAML on the STA thread with the app's dictionaries and reads the shown text back, the way
/// <see cref="SpeedTestViewUiTests"/> does. Neither WMI nor <c>powercfg</c> is touched: both reads are supplied.
/// </remarks>
public sealed class BatteryHealthViewUiTests
{
    private static readonly BatteryInfo Laptop = new()
    {
        HasBattery = true,
        Name = "Primary",
        ChargePercent = 80,
        Status = "Charging",
    };

    private static readonly DateTime FirstWeek = new(2026, 4, 6);

    private static BatteryReportRead History(params (int Day, double Percent)[] points) => new(
        BatteryReportOutcome.Read,
        [.. points.Select(p => new BatteryCapacityPoint(FirstWeek.AddDays(p.Day), 50000, (long)Math.Round(p.Percent * 500)))]);

    /// <summary>Built off the STA thread and handed to the view, as the Speed Test view tests do.</summary>
    private static async Task<BatteryHealthViewModel> VmAsync(BatteryInfo battery, BatteryReportRead history)
    {
        var vm = new BatteryHealthViewModel(() => Task.FromResult<BatteryInfo?>(battery), _ => Task.FromResult(history));
        await vm.InitializationComplete;
        return vm;
    }

    private static BatteryHealthView Laid(BatteryHealthViewModel vm)
    {
        AppResources.Ensure();
        var view = new BatteryHealthView { DataContext = vm };
        view.Measure(new Size(1200, 2400));
        view.Arrange(new Rect(0, 0, 1200, 2400));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task SixMonthsOfHistory_ShowTheVerdictAndTheChart_AndNothingElse()
    {
        var vm = await VmAsync(Laptop, History((0, 100), (91, 98), (182, 96)));

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var card = CardTitled(view, "Capacity over time");
            var shown = ShownTexts(card);

            Assert.Contains("Normal wear", shown);
            Assert.Contains(vm.WearVerdict!.Detail, shown);
            Assert.Contains("From Windows' battery report: a point a week, and a point a day for the last few days.", shown);
            Assert.Equal(Visibility.Visible, InnerCardOf(ChartNamed(view, "Battery capacity over time")).Visibility);
            Assert.DoesNotContain("Not enough history yet", shown);
        });
    }

    [Fact]
    public async Task AShortHistory_ShowsHowMuchThereIsSoFar_InPlaceOfTheChart()
    {
        var vm = await VmAsync(Laptop, History((0, 100), (14, 99)));

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(CardTitled(view, "Capacity over time"));

            Assert.Contains("Not enough history yet", shown);
            Assert.Contains("Windows has recorded 2 weeks so far. The trend appears after about a month.", shown);
            Assert.Equal(Visibility.Collapsed, InnerCardOf(ChartNamed(view, "Battery capacity over time")).Visibility);
            Assert.DoesNotContain("Normal wear", shown);
        });
    }

    [Fact]
    public async Task AHistoryWindowsDoesNotKeep_IsOneLine_WithNoChart()
    {
        var vm = await VmAsync(Laptop, new BatteryReportRead(BatteryReportOutcome.NoHistory, []));

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(CardTitled(view, "Capacity over time"));

            Assert.Contains(BatteryHealthViewModel.NoHistoryKept, shown);
            Assert.Equal(Visibility.Collapsed, InnerCardOf(ChartNamed(view, "Battery capacity over time")).Visibility);
            Assert.DoesNotContain("Not enough history yet", shown);
        });
    }

    [Fact]
    public async Task WithoutABattery_TheCardIsNotShown()
    {
        var vm = await VmAsync(new BatteryInfo(), History((0, 100), (182, 96)));

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.DoesNotContain("Capacity over time", ShownTexts(view));
            Assert.Contains("No battery detected on this device.", ShownTexts(view));
        });
    }

    // ── Figures Windows did not give (#2623) ──
    //
    // Without administrator rights the three reads are refused, and the details card read "0 mWh", "0 mWh" and "0".

    [Fact]
    public async Task TheDetails_SayNotAvailable_ForWhatWindowsDidNotGive()
    {
        var vm = await VmAsync(Laptop, History((0, 100), (182, 96)));

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Equal("Not available", ValueOf(shown, "Design capacity"));
            Assert.Equal("Not available", ValueOf(shown, "Full charge capacity"));
            Assert.Equal("Not available", ValueOf(shown, "Cycle count"));
        });
    }

    [Fact]
    public async Task TheDetails_ShowWhatWindowsGave_IncludingACycleCountOfZero()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            Name = "Primary",
            ChargePercent = 80,
            Status = "Charging",
            DesignCapacityMWh = 57000,
            FullChargeCapacityMWh = 51300,
            CycleCountRead = true,
        };
        var vm = await VmAsync(battery, History((0, 100), (182, 96)));

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Equal("57000 mWh", ValueOf(shown, "Design capacity"));
            Assert.Equal("51300 mWh", ValueOf(shown, "Full charge capacity"));
            Assert.Equal("0", ValueOf(shown, "Cycle count"));
        });
    }

    /// <summary>The figure the details card shows beside <paramref name="label"/>, which comes right after it.</summary>
    private static string ValueOf(List<string> shown, string label)
    {
        var at = shown.IndexOf(label);
        Assert.True(at >= 0 && at + 1 < shown.Count, $"no \"{label}\" row: {string.Join(" | ", shown)}");
        return shown[at + 1];
    }

    /// <summary>
    /// The text actually on screen under <paramref name="root"/>: a collapsed element still holds its bound text, so its
    /// subtree is skipped rather than read.
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

    /// <summary>The CardInner border the chart sits in, which is what its visibility is bound on.</summary>
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

    // A collapsed subtree is never laid out, so a chart hidden from the start exists only logically.
    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in LogicalDescendants<T>(child)) yield return deeper;
        }
    }
}
