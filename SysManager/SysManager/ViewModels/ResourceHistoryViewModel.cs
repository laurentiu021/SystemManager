// SysManager · ResourceHistoryViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using Serilog;
using SkiaSharp;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Shows the history recorded by <see cref="ResourceHistoryService"/> as a scrollable
/// usage chart (CPU / RAM / GPU %) and a temperature chart (CPU / GPU °C), with a
/// selectable time range and CSV export. Read-only: it never modifies the system and the
/// CSV is written only to a file the user picks. Nothing leaves the machine.
/// </summary>
public sealed partial class ResourceHistoryViewModel : ViewModelBase
{
    // A chart renders cleanly with a few hundred points; more just costs CPU with no
    // visible benefit, so any range is downsampled to this cap before plotting.
    private const int MaxChartPoints = 400;

    private readonly ResourceHistoryService _service;
    private IReadOnlyList<ResourceSample> _loaded = [];

    public sealed record RangeOption(string Label, TimeSpan Range);

    public IReadOnlyList<RangeOption> RangeOptions { get; } =
    [
        new("Last hour", TimeSpan.FromHours(1)),
        new("Last 6 hours", TimeSpan.FromHours(6)),
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days", TimeSpan.FromDays(7)),
        new("Last 30 days", TimeSpan.FromDays(30)),
    ];

    public int[] RetentionOptions => ResourceHistoryService.RetentionOptions;

    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private int _sampleCount;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasTemperatureData;
    [ObservableProperty] private string _summary = "";

    // ── Charts (built once, buffers replaced on reload) ───────────────────
    public ObservableCollection<ISeries> UsageSeries { get; } = new();
    public Axis[] UsageXAxes { get; }
    public Axis[] UsageYAxes { get; }

    public ObservableCollection<ISeries> TemperatureSeries { get; } = new();
    public Axis[] TemperatureXAxes { get; }
    public Axis[] TemperatureYAxes { get; }

    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    private readonly BulkObservableCollection<DateTimePoint> _cpuBuffer = new();
    private readonly BulkObservableCollection<DateTimePoint> _ramBuffer = new();
    private readonly BulkObservableCollection<DateTimePoint> _gpuBuffer = new();
    private readonly BulkObservableCollection<DateTimePoint> _cpuTempBuffer = new();
    private readonly BulkObservableCollection<DateTimePoint> _gpuTempBuffer = new();

    public ResourceHistoryViewModel(ResourceHistoryService service)
    {
        _service = service;
        _retentionDays = service.RetentionDays;
        _selectedRange = RangeOptions[2]; // Last 24 hours

        UsageXAxes = [BuildTimeAxis()];
        UsageYAxes = [BuildPercentAxis()];
        TemperatureXAxes = [BuildTimeAxis()];
        TemperatureYAxes = [BuildTempAxis()];

        // Paint chart labels/legend/tooltip from the active theme and keep them in sync,
        // so axis text stays readable on the light presets (a hardcoded near-white was
        // invisible on a white background). Unsubscribed in Dispose(bool).
        ApplyChartTheme();
        ThemeService.Instance.ThemeChanged += ApplyChartTheme;

        UsageSeries.Add(BuildLine("CPU", "#60A5FA", _cpuBuffer));
        UsageSeries.Add(BuildLine("RAM", "#A78BFA", _ramBuffer));
        UsageSeries.Add(BuildLine("GPU", "#34D399", _gpuBuffer));
        TemperatureSeries.Add(BuildLine("CPU °C", "#F59E0B", _cpuTempBuffer));
        TemperatureSeries.Add(BuildLine("GPU °C", "#EF4444", _gpuTempBuffer));

        InitializeAsync(() => ReloadAsync());
    }

    partial void OnSelectedRangeChanged(RangeOption value) => _ = ReloadAsync();

    partial void OnRetentionDaysChanged(int value)
    {
        _service.RetentionDays = value;
        StatusMessage = $"Keeping {value} days of history.";
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            _loaded = await _service.LoadAsync(SelectedRange.Range);
            var points = ResourceHistoryService.Downsample(_loaded, MaxChartPoints);

            _cpuBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.CpuPercent)));
            _ramBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.RamPercent)));
            _gpuBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.GpuPercent)));
            _cpuTempBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.CpuTempC)));
            _gpuTempBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.GpuTempC)));

            SampleCount = _loaded.Count;
            HasData = _loaded.Count > 0;
            // The temperature chart is meaningful only if at least one sample carries a temp
            // reading (sensors/admin dependent); otherwise show its empty state, not a blank.
            HasTemperatureData = _loaded.Any(s => s.CpuTempC.HasValue || s.GpuTempC.HasValue);
            Summary = BuildSummary(_loaded);
            StatusMessage = HasData
                ? $"Showing {_loaded.Count} sample(s) over {SelectedRange.Label.ToLowerInvariant()}."
                : "No history yet — samples are recorded every 10 seconds while the app runs.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Pure: averages/peaks for the summary strip. Testable without WPF.</summary>
    internal static string BuildSummary(IReadOnlyList<ResourceSample> samples)
    {
        if (samples.Count == 0) return "";
        double avgCpu = samples.Average(s => s.CpuPercent);
        double maxCpu = samples.Max(s => s.CpuPercent);
        double avgRam = samples.Average(s => s.RamPercent);
        double maxRam = samples.Max(s => s.RamPercent);
        var sb = new StringBuilder();
        sb.Append($"CPU avg {avgCpu:F0}% · peak {maxCpu:F0}%   ·   RAM avg {avgRam:F0}% · peak {maxRam:F0}%");
        var temps = samples.Where(s => s.CpuTempC.HasValue).Select(s => s.CpuTempC!.Value).ToList();
        if (temps.Count > 0)
            sb.Append($"   ·   CPU temp peak {temps.Max():F0}°C");
        return sb.ToString();
    }

    [RelayCommand(CanExecute = nameof(HasData))]
    private async Task ExportCsvAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-Resources-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var csv = ResourceHistoryService.ToCsv(_loaded);
            await File.WriteAllTextAsync(dlg.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Exported {_loaded.Count} sample(s) to {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("Resource history exported", Path.GetFileName(dlg.FileName));
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnHasDataChanged(bool value) => ExportCsvCommand.NotifyCanExecuteChanged();

    // ── Chart factories (mirror NetworkSharedState's idiom) ────────────────

    private static LineSeries<DateTimePoint> BuildLine(string name, string hex, BulkObservableCollection<DateTimePoint> values)
    {
        var color = SKColor.Parse(hex.TrimStart('#')).WithAlpha(230);
        return new LineSeries<DateTimePoint>
        {
            Name = name,
            Values = values,
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0.3,
            Stroke = new SolidColorPaint(color, 2),
            AnimationsSpeed = TimeSpan.Zero
        };
    }

    private static Axis BuildTimeAxis() => new()
    {
        Labeler = v => v > 0 && v < DateTime.MaxValue.Ticks ? new DateTime((long)v).ToString("MM-dd HH:mm") : "",
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    private static Axis BuildPercentAxis() => new()
    {
        Name = "Usage (%)",
        MinLimit = 0,
        MaxLimit = 100,
        TextSize = 13,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        Labeler = v => $"{v:F0}%",
        NameTextSize = 14
    };

    private static Axis BuildTempAxis() => new()
    {
        Name = "Temperature (°C)",
        MinLimit = 0,
        TextSize = 13,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        Labeler = v => $"{v:F0}°C",
        NameTextSize = 14
    };

    /// <summary>
    /// Repaints the chart labels, legend, and tooltip from the current app theme. Wired to
    /// <see cref="ThemeService.ThemeChanged"/> so switching to a light preset makes the axis
    /// text dark-on-light (readable) instead of the previous hardcoded near-white.
    /// </summary>
    private void ApplyChartTheme() => ChartTheme.Apply(
        LegendTextPaint, TooltipTextPaint, TooltipBackgroundPaint,
        [.. UsageXAxes, .. UsageYAxes, .. TemperatureXAxes, .. TemperatureYAxes]);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeService.Instance.ThemeChanged -= ApplyChartTheme;
            foreach (var s in UsageSeries) DisposeSeries(s);
            foreach (var s in TemperatureSeries) DisposeSeries(s);
            DisposeAxisPaints(UsageXAxes);
            DisposeAxisPaints(UsageYAxes);
            DisposeAxisPaints(TemperatureXAxes);
            DisposeAxisPaints(TemperatureYAxes);
            LegendTextPaint.SKTypeface?.Dispose();
            (LegendTextPaint as IDisposable)?.Dispose();
            (LegendBackgroundPaint as IDisposable)?.Dispose();
            (TooltipTextPaint as IDisposable)?.Dispose();
            (TooltipBackgroundPaint as IDisposable)?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void DisposeSeries(ISeries series)
    {
        if (series is LineSeries<DateTimePoint> line)
        {
            (line.Stroke as IDisposable)?.Dispose();
            (line.Fill as IDisposable)?.Dispose();
        }
    }

    private static void DisposeAxisPaints(Axis[] axes)
    {
        foreach (var axis in axes)
        {
            // Free the native SKTypeface handles before the paints, mirroring how the legend
            // typeface is released in Dispose — SolidColorPaint.Dispose does not free them, so the
            // per-axis "Segoe UI" typefaces (BuildTimeAxis/BuildPercentAxis/BuildTempAxis) leak.
            (axis.NamePaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.LabelsPaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.NamePaint as IDisposable)?.Dispose();
            (axis.LabelsPaint as IDisposable)?.Dispose();
            (axis.SeparatorsPaint as IDisposable)?.Dispose();
        }
    }
}
