// SysManager · SpeedTrendChart
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
/// One engine's saved speed tests drawn as a trend: download and upload over time, a dashed line at the usual
/// download, and one sentence saying how the runs compare with it (#1499).
/// </summary>
/// <remarks>
/// <para>One instance per history card. Ookla and HTTP measure differently, so drawn on one axis a switch of
/// engine would read as "my speed dropped". The chart draws the list the card's table binds, so nothing new is
/// measured or stored.</para>
/// <para>Paints, axes and typefaces are SkiaSharp objects holding native handles. They are built once, repainted
/// in place on a theme change and released by <see cref="Dispose"/>, the way <see cref="ResourceHistoryViewModel"/>
/// does it.</para>
/// </remarks>
public sealed partial class SpeedTrendChart : ObservableObject, IDisposable
{
    // Colours from the palette ChartThemeTests checks on every preset: blue for download and green for upload,
    // as Resource History draws CPU and GPU. The usual line is the download colour, dashed, because it IS the
    // usual download; a third colour would ask the legend to explain what the dash already says.
    private const string DownloadHex = "#60A5FA";
    private const string UploadHex = "#34D399";

    private readonly BulkObservableCollection<DateTimePoint> _download = new();
    private readonly BulkObservableCollection<DateTimePoint> _upload = new();
    private readonly BulkObservableCollection<DateTimePoint> _usual = new();

    /// <summary>Line strokes keyed by their designed colour, so a theme change re-derives each from its base.</summary>
    private readonly List<KeyValuePair<SKColor, SolidColorPaint>> _seriesStrokes = [];

    private bool _disposed;

    /// <summary>Download, upload and usual-download lines, in that order.</summary>
    public ObservableCollection<ISeries> Series { get; } = new();

    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    /// <summary>True when there are enough measured runs to draw a line.</summary>
    [ObservableProperty] private bool _hasTrend;

    /// <summary>True when exactly one run is measured, so the card says a trend needs a second.</summary>
    [ObservableProperty] private bool _hasSingleRun;

    /// <summary>The sentence under the chart; empty while there is no trend.</summary>
    [ObservableProperty] private string _summary = "";

    public SpeedTrendChart()
    {
        XAxes = [BuildDateAxis()];
        YAxes = [BuildMbpsAxis()];

        // Series before the first theme pass, so the strokes are readable from the first frame on a light preset.
        Series.Add(BuildLine("Download", DownloadHex, _download, dashed: false));
        Series.Add(BuildLine("Upload", UploadHex, _upload, dashed: false));
        Series.Add(BuildLine("Usual download", DownloadHex, _usual, dashed: true));

        ApplyChartTheme();
        ThemeService.Instance.ThemeChanged += ApplyChartTheme;
    }

    /// <summary>
    /// Redraws from <paramref name="history"/>, which may be in any order. Only runs that measured a download are
    /// plotted, as <see cref="SpeedTrendAnalyzer.Measured"/> decides.
    /// </summary>
    public void Update(IEnumerable<SpeedTestResult> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (_disposed) return;

        var measured = SpeedTrendAnalyzer.Measured(history);
        var usual = SpeedTrendAnalyzer.UsualDownload(measured);

        if (usual is { } u)
        {
            _download.ReplaceWith(measured.Select(r => new DateTimePoint(r.CompletedAt, r.DownloadMbps)));
            // A run whose upload was not measured leaves a gap, not a zero: zero would draw a drop that never happened.
            _upload.ReplaceWith(measured.Select(r => new DateTimePoint(r.CompletedAt, r.UploadMbps)));
            _usual.ReplaceWith([new DateTimePoint(measured[0].CompletedAt, u), new DateTimePoint(measured[^1].CompletedAt, u)]);
        }
        else
        {
            _download.ReplaceWith([]);
            _upload.ReplaceWith([]);
            _usual.ReplaceWith([]);
        }

        HasTrend = usual is not null;
        HasSingleRun = measured.Count == 1;
        Summary = SpeedTrendAnalyzer.Describe(measured) ?? "";
    }

    private LineSeries<DateTimePoint> BuildLine(
        string name, string hex, BulkObservableCollection<DateTimePoint> values, bool dashed)
    {
        var color = SKColor.Parse(hex.TrimStart('#')).WithAlpha(230);
        var stroke = new SolidColorPaint(color, dashed ? 1.5f : 2);
        if (dashed) stroke.PathEffect = new DashEffect([6, 4]);
        _seriesStrokes.Add(new(color, stroke));
        return new LineSeries<DateTimePoint>
        {
            Name = name,
            Values = values,
            Fill = null,
            GeometrySize = 0,
            // Straight segments: these are separate runs, and a curve would invent speeds between them.
            LineSmoothness = 0,
            Stroke = stroke,
            AnimationsSpeed = TimeSpan.Zero,
            XToolTipLabelFormatter = p => ChartAxisLabels.Time(p.Coordinate.SecondaryValue, "d MMM, HH:mm"),
            YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("F1", CultureInfo.InvariantCulture) + " Mbps",
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

    private static Axis BuildMbpsAxis() => new()
    {
        Name = "Mbps",
        MinLimit = 0,
        TextSize = 12,
        NameTextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        Labeler = v => v.ToString("F0", CultureInfo.InvariantCulture)
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
