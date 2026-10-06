// SysManager · BatteryCapacityChartTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BatteryCapacityChart"/>: what the battery's capacity history draws, and on what scale (#1513).
/// </summary>
/// <remarks>
/// The chart's data is plain collections and axis settings, so it is read back without rendering anything.
/// </remarks>
public sealed class BatteryCapacityChartTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 3, 5);
    private readonly BatteryCapacityChart _chart = new();

    public void Dispose() => _chart.Dispose();

    private static List<BatteryCapacityPoint> History(params (int Day, double Percent)[] points) =>
        [.. points.Select(p => new BatteryCapacityPoint(Start.AddDays(p.Day), 50000, (long)Math.Round(p.Percent * 500)))];

    private IReadOnlyList<DateTimePoint> Points(int series) =>
        [.. ((LineSeries<DateTimePoint>)_chart.Series[series]).Values!];

    [Fact]
    public void ANewChart_ShowsNothing_AndHasItsTwoLines()
    {
        Assert.False(_chart.HasChart);
        Assert.Equal(new[] { "Holds when full (% of new)", "When new (100%)" }, _chart.Series.Select(s => s.Name));
        Assert.Single(_chart.XAxes);
        Assert.Single(_chart.YAxes);
    }

    [Fact]
    public void AHistory_IsDrawnAsPercentOfNew_WithTheWhenNewLineAcrossIt()
    {
        _chart.Update(History((0, 100), (7, 99), (14, 98)));

        Assert.True(_chart.HasChart);
        Assert.Equal(new[] { 100d, 99d, 98d }, Points(0).Select(p => p.Value!.Value));
        Assert.Equal(new[] { Start, Start.AddDays(7), Start.AddDays(14) }, Points(0).Select(p => p.DateTime));

        var whenNew = Points(1);
        Assert.Equal(new[] { Start, Start.AddDays(14) }, whenNew.Select(p => p.DateTime));
        Assert.All(whenNew, p => Assert.Equal(100, p.Value));
    }

    [Fact]
    public void OneEntry_IsNeverDrawnAsALine()
    {
        _chart.Update(History((0, 100), (7, 99)));
        Assert.True(_chart.HasChart);

        _chart.Update(History((0, 100)));

        Assert.False(_chart.HasChart);
        Assert.Empty(Points(0));
        Assert.Empty(Points(1));
    }

    [Fact]
    public void AnUpdate_SetsTheScale()
    {
        _chart.Update(History((0, 100), (182, 88)));

        var y = _chart.YAxes[0];
        Assert.Equal(70d, y.MinLimit);
        Assert.Equal(105d, y.MaxLimit);
        Assert.Equal(new[] { 70d, 80d, 90d, 100d }, y.CustomSeparators);
        Assert.Equal("88%", y.Labeler(88));
    }

    [Theory]
    [InlineData(100, 88, 70, 105)]
    [InlineData(100, 76, 70, 105)]
    [InlineData(100, 74, 60, 105)]
    [InlineData(100, 3, 0, 105)]
    [InlineData(102, 90, 70, 110)]
    [InlineData(104.5, 90, 70, 110)]
    [InlineData(90, 80, 70, 105)]
    public void TheScale_StartsAt70OrLower_AndEndsClearOfBothLines(
        double highest, double lowest, double floor, double ceiling)
    {
        // Zoomed to the data, a loss of two points would fill the chart from top to bottom.
        var limits = BatteryCapacityChart.AxisLimits(History((0, highest), (30, lowest)));

        Assert.Equal((floor, ceiling), limits);
    }

    [Theory]
    [InlineData(70, 105, new[] { 70d, 80, 90, 100 })]
    [InlineData(60, 110, new[] { 60d, 70, 80, 90, 100, 110 })]
    [InlineData(0, 105, new[] { 0d, 20, 40, 60, 80, 100 })]
    [InlineData(30, 105, new[] { 40d, 60, 80, 100 })]
    public void TheLabels_AlwaysMark100_EveryTenOrEveryTwenty(double floor, double ceiling, double[] expected)
        => Assert.Equal(expected, BatteryCapacityChart.Separators(floor, ceiling));

    [Fact]
    public void UnderAYear_TheDatesAreDayAndMonth()
    {
        _chart.Update(History((0, 100), (200, 95)));

        var x = _chart.XAxes[0];
        Assert.Equal("5 Mar", x.Labeler(Start.Ticks));
        Assert.Equal((double)TimeSpan.FromDays(1).Ticks, x.MinStep);
    }

    [Fact]
    public void FromAYear_TheDatesAreMonthAndYear()
    {
        _chart.Update(History((0, 100), (365, 92)));

        var x = _chart.XAxes[0];
        Assert.Equal("Mar 2026", x.Labeler(Start.Ticks));
        Assert.Equal((double)TimeSpan.FromDays(31).Ticks, x.MinStep);
    }

    [Fact]
    public void TheDateAxis_LabelsAPlausibleDateOnly()
    {
        // With nothing to plot an axis lays ticks near zero, which format as dates in the year 1 (#2371).
        _chart.Update(History((0, 100), (200, 95)));

        Assert.Equal("", _chart.XAxes[0].Labeler(1000));
    }

    [Fact]
    public void ADisposedChart_IgnoresUpdates_AndCanBeDisposedTwice()
    {
        var chart = new BatteryCapacityChart();
        chart.Dispose();

        chart.Update(History((0, 100), (7, 99)));
        chart.Dispose();

        Assert.False(chart.HasChart);
    }

    [Fact]
    public void Null_IsRejected() => Assert.Throws<ArgumentNullException>(() => _chart.Update(null!));
}
