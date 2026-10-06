// SysManager · BatteryCapacityChart
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// A battery's capacity history drawn as one line: what it holds when full, as a percentage of what it held when
/// new, with a dashed line at 100% (#1513).
/// </summary>
/// <remarks>
/// <para>Percent of new rather than mWh, so the axis reads like the Health figure above it. The range starts at 70%
/// or lower and ends just above 100%, so a loss of two points draws as the small change it is instead of filling the
/// chart from top to bottom.</para>
/// <para>Paints, axes and typefaces are SkiaSharp objects holding native handles. They are built once, repainted in
/// place on a theme change and released by <see cref="Dispose"/>, the way <see cref="SpeedTrendChart"/> does it.</para>
/// </remarks>
public sealed partial class BatteryCapacityChart : ObservableObject, IDisposable
{
    // From the palette ChartThemeTests checks on every preset. The 100% line is the same colour, dashed, because it is
    // the same quantity: what the battery held when it was new.
    private const string CapacityHex = "#60A5FA";

    /// <summary>The highest the value axis starts, so the chart never zooms in closer than 70% to 100%.</summary>
    private const double HighestFloor = 70;

    private readonly BulkObservableCollection<DateTimePoint> _capacity = new();
    private readonly BulkObservableCollection<DateTimePoint> _whenNew = new();

    /// <summary>Line strokes keyed by their designed colour, so a theme change re-derives each from its base.</summary>
    private readonly List<KeyValuePair<SKColor, SolidColorPaint>> _seriesStrokes = [];

    private bool _disposed;

    /// <summary>The capacity line and the 100% line, in that order.</summary>
    public ObservableCollection<ISeries> Series { get; } = new();

    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    /// <summary>True when there are at least two entries, so there is a line to draw.</summary>
    [ObservableProperty] private bool _hasChart;

    public BatteryCapacityChart()
    {
        XAxes = [BuildDateAxis()];
        YAxes = [BuildPercentAxis()];

        // Series before the first theme pass, so the strokes are readable from the first frame on a light preset.
        Series.Add(BuildLine("Holds when full (% of new)", _capacity, dashed: false));
        Series.Add(BuildLine("When new (100%)", _whenNew, dashed: true));

        ApplyChartTheme();
        ThemeService.Instance.ThemeChanged += ApplyChartTheme;
    }

    /// <summary>
    /// Redraws from <paramref name="history"/>, oldest first as <see cref="BatteryReportParser"/> returns it. Fewer than
    /// two entries clear the chart, so one reading is never drawn as a line.
    /// </summary>
    public void Update(IReadOnlyList<BatteryCapacityPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (_disposed) return;

        if (history.Count < 2)
        {
            _capacity.ReplaceWith([]);
            _whenNew.ReplaceWith([]);
            HasChart = false;
            return;
        }

        _capacity.ReplaceWith(history.Select(p => new DateTimePoint(p.Date, p.HealthPercent)));
        _whenNew.ReplaceWith([new DateTimePoint(history[0].Date, 100), new DateTimePoint(history[^1].Date, 100)]);

        var (floor, ceiling) = AxisLimits(history);
        var y = YAxes[0];
        y.MinLimit = floor;
        y.MaxLimit = ceiling;
        y.CustomSeparators = Separators(floor, ceiling);

        var (format, minStep) = DateAxisFor(history[^1].Date - history[0].Date);
        var x = XAxes[0];
        x.Labeler = v => ChartAxisLabels.Time(v, format);
        x.MinStep = minStep.Ticks;

        HasChart = true;
    }

    /// <summary>
    /// The value axis' range: from 70%, or lower when the line goes within five points of that, to at least five points
    /// above both 100% and the highest entry, so neither line sits on the chart's edge.
    /// </summary>
    internal static (double Floor, double Ceiling) AxisLimits(IReadOnlyList<BatteryCapacityPoint> history)
    {
        var lowest = history.Min(p => p.HealthPercent);
        var highest = history.Max(p => p.HealthPercent);
        var floor = Math.Max(0, Math.Min(HighestFloor, Math.Floor((lowest - 5) / 10) * 10));
        var ceiling = Math.Max(105, Math.Ceiling((highest + 5) / 5) * 5);
        return (floor, ceiling);
    }

    /// <summary>
    /// The value axis' labels: every ten points, or every twenty on a tall range, always including 100% because that is
    /// what the dashed line marks.
    /// </summary>
    internal static double[] Separators(double floor, double ceiling)
    {
        var step = ceiling - floor > 60 ? 20 : 10;
        List<double> marks = [];
        for (var v = 100.0; v >= floor; v -= step) marks.Add(v);
        for (var v = 100.0 + step; v <= ceiling; v += step) marks.Add(v);
        return [.. marks.Order()];
    }

    /// <summary>
    /// The date labels for a history spanning <paramref name="span"/>: day and month under a year, month and year
    /// beyond it. The minimum step keeps two ticks from landing on the same day, or the same month, and printing the
    /// same label twice.
    /// </summary>
    internal static (string Format, TimeSpan MinStep) DateAxisFor(TimeSpan span) =>
        span < TimeSpan.FromDays(365) ? ("d MMM", TimeSpan.FromDays(1)) : ("MMM yyyy", TimeSpan.FromDays(31));

    private LineSeries<DateTimePoint> BuildLine(string name, BulkObservableCollection<DateTimePoint> values, bool dashed)
    {
        var color = SKColor.Parse(CapacityHex.TrimStart('#')).WithAlpha(230);
        var stroke = new SolidColorPaint(color, dashed ? 1.5f : 2);
        if (dashed) stroke.PathEffect = new DashEffect([6, 4]);
        _seriesStrokes.Add(new(color, stroke));
        return new LineSeries<DateTimePoint>
        {
            Name = name,
            Values = values,
            Fill = null,
            GeometrySize = 0,
            // Straight segments: each entry is Windows' own reading for its period, and a curve would invent readings
            // between them.
            LineSmoothness = 0,
            Stroke = stroke,
            AnimationsSpeed = TimeSpan.Zero,
            XToolTipLabelFormatter = p => ChartAxisLabels.Time(p.Coordinate.SecondaryValue, "d MMM yyyy"),
            YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("F0", CultureInfo.InvariantCulture) + "%",
        };
    }

    private static Axis BuildDateAxis() => new()
    {
        Labeler = v => ChartAxisLabels.Time(v, "d MMM"),
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    private static Axis BuildPercentAxis() => new()
    {
        MinLimit = HighestFloor,
        MaxLimit = 105,
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        Labeler = v => v.ToString("F0", CultureInfo.InvariantCulture) + "%"
    };

    private void ApplyChartTheme() => ChartTheme.Apply(
        LegendTextPaint, TooltipTextPaint, TooltipBackgroundPaint,
        [.. XAxes, .. YAxes],
        seriesBaseColors: _seriesStrokes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ThemeService.Instance.ThemeChanged -= ApplyChartTheme;
        foreach (var series in Series)
        {
            if (series is not LineSeries<DateTimePoint> line) continue;
            if (line.Stroke is SolidColorPaint stroke) (stroke.PathEffect as IDisposable)?.Dispose();
            (line.Stroke as IDisposable)?.Dispose();
            (line.Fill as IDisposable)?.Dispose();
        }

        foreach (var axis in XAxes.Concat(YAxes))
        {
            // The typefaces first: disposing a paint does not release the SKTypeface it holds.
            (axis.NamePaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.LabelsPaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.NamePaint as IDisposable)?.Dispose();
            (axis.LabelsPaint as IDisposable)?.Dispose();
            (axis.SeparatorsPaint as IDisposable)?.Dispose();
        }

        LegendTextPaint.SKTypeface?.Dispose();
        (LegendTextPaint as IDisposable)?.Dispose();
        (LegendBackgroundPaint as IDisposable)?.Dispose();
        (TooltipTextPaint as IDisposable)?.Dispose();
        (TooltipBackgroundPaint as IDisposable)?.Dispose();
    }
}
