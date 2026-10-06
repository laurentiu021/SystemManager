// SysManager · SpeedTrendChartTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedTrendChart"/>: what the Speed Test trend draws, and when it draws nothing (#1499).
/// </summary>
/// <remarks>
/// The chart's data is plain collections, so it can be read back without rendering anything. Every run is
/// constructed with a fixed date; nothing here touches the network or the user's history file.
/// </remarks>
public sealed class SpeedTrendChartTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 26, 20, 0, 0);
    private readonly SpeedTrendChart _chart = new();

    public void Dispose() => _chart.Dispose();

    private static SpeedTestResult Run(double down, int day, double? up = 48) =>
        new("Ookla", down, up, 9, "Bucharest", Start.AddDays(day));

    private IReadOnlyList<DateTimePoint> Points(string seriesName) =>
        [.. ((LineSeries<DateTimePoint>)_chart.Series.Single(s => s.Name == seriesName)).Values!];

    [Fact]
    public void ANewChart_ShowsNothing_AndHasItsThreeLines()
    {
        Assert.False(_chart.HasTrend);
        Assert.False(_chart.HasSingleRun);
        Assert.Equal("", _chart.Summary);
        Assert.Equal(new[] { "Download", "Upload", "Usual download" }, _chart.Series.Select(s => s.Name));
        Assert.Single(_chart.XAxes);
        Assert.Single(_chart.YAxes);
    }

    [Fact]
    public void OneMeasuredRun_IsNeverDrawnAsALine()
    {
        _chart.Update([Run(480, 0)]);

        Assert.False(_chart.HasTrend);
        Assert.True(_chart.HasSingleRun);
        Assert.Equal("", _chart.Summary);
        Assert.Empty(Points("Download"));
    }

    [Fact]
    public void TwoRuns_DrawTheLines_OldestFirst_WhateverOrderTheyCameIn()
    {
        // The history lists are newest first; the chart reads left to right in time.
        _chart.Update([Run(500, 2, up: 51), Run(470, 0, up: 47), Run(480, 1, up: 49)]);

        Assert.True(_chart.HasTrend);
        Assert.False(_chart.HasSingleRun);
        Assert.Equal(new[] { 470d, 480d, 500d }, Points("Download").Select(p => p.Value!.Value));
        Assert.Equal(new[] { 47d, 49d, 51d }, Points("Upload").Select(p => p.Value!.Value));
        Assert.Equal(new[] { Start, Start.AddDays(1), Start.AddDays(2) }, Points("Download").Select(p => p.DateTime));
        Assert.Equal("Usually about 480 Mbps.", _chart.Summary);
    }

    [Fact]
    public void TheUsualLine_RunsAtTheMedian_AcrossTheWholeRange()
    {
        _chart.Update([Run(470, 0), Run(150, 1), Run(500, 2)]);

        var usual = Points("Usual download");
        Assert.Equal(2, usual.Count);
        Assert.All(usual, p => Assert.Equal(470, p.Value));
        Assert.Equal(Start, usual[0].DateTime);
        Assert.Equal(Start.AddDays(2), usual[1].DateTime);
    }

    [Fact]
    public void AnUnmeasuredUpload_LeavesAGap_NotAZero()
    {
        // Zero would draw a drop to nothing that never happened.
        _chart.Update([Run(470, 0), Run(480, 1, up: null), Run(500, 2)]);

        Assert.Null(Points("Upload")[1].Value);
        Assert.Equal(3, Points("Download").Count);
    }

    [Fact]
    public void ARunThatMeasuredNothing_IsLeftOut()
    {
        _chart.Update([Run(470, 0), Run(0, 1), Run(500, 2)]);

        Assert.Equal(new[] { 470d, 500d }, Points("Download").Select(p => p.Value!.Value));
    }

    [Fact]
    public void AHistoryThatEmpties_TakesTheTrendAway()
    {
        _chart.Update([Run(470, 0), Run(480, 1)]);
        Assert.True(_chart.HasTrend);

        _chart.Update([]);

        Assert.False(_chart.HasTrend);
        Assert.False(_chart.HasSingleRun);
        Assert.Equal("", _chart.Summary);
        Assert.Empty(Points("Download"));
        Assert.Empty(Points("Upload"));
        Assert.Empty(Points("Usual download"));
    }

    [Fact]
    public void ADisposedChart_IgnoresUpdates_AndCanBeDisposedTwice()
    {
        var chart = new SpeedTrendChart();
        chart.Dispose();

        chart.Update([Run(470, 0), Run(480, 1)]);
        chart.Dispose();

        Assert.False(chart.HasTrend);
    }

    [Fact]
    public void TheAxes_LabelAPlausibleDateOnly()
    {
        // The guard Resource History's axis taught: with nothing to plot, an axis lays ticks near zero, which
        // format as dates in the year 1 (#2371). Only a real date may be printed.
        var label = _chart.XAxes[0].Labeler;
        Assert.Equal("", label(1000));
        Assert.Equal("26 Sep", label(Start.Ticks));
    }
}
