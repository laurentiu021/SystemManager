// SysManager · NetworkSharedState
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Shared state for the four Network sub-ViewModels. Owns the pinger,
/// tracer, targets, chart buffers, health diagnostic and the UI flush pump.
/// </summary>
public sealed partial class NetworkSharedState : ObservableObject, IDisposable
{
    private const int UiFlushIntervalMs = 250;
    private const int JitterSampleWindow = 20;

    internal static readonly string[] Palette =
    {
        "#4CC9F0", "#80FFDB", "#F72585", "#FFD166",
        "#B388FF", "#06D6A0", "#FF6B6B", "#F8961E",
    };

    internal readonly PingMonitorService Pinger;
    internal readonly TracerouteService Tracer;
    internal readonly TracerouteMonitorService TraceMonitor;
    internal readonly SpeedTestService Speed;
    internal readonly NetworkRepairService Repair;
    internal readonly Dispatcher? Dispatcher;
    internal readonly DispatcherTimer? FlushTimer;
    internal readonly ConcurrentQueue<PingSample> Pending = new();
    internal int PaletteIndex;

    internal readonly ConcurrentDictionary<string, BulkObservableCollection<DateTimePoint>> Buffers = new();

    private bool _disposed;
    internal readonly ConcurrentDictionary<string, BulkObservableCollection<ObservablePoint>> TraceBuffers = new();
    internal readonly Dictionary<string, IReadOnlyList<TracerouteHop>> LatestRoutes = new();
    private readonly Dictionary<string, PropertyChangedEventHandler> _targetHandlers = new();

    public ObservableCollection<PingTarget> Targets { get; } = new();
    public BulkObservableCollection<TracerouteHop> TracerouteHops { get; } = new();

    // ── Chart infrastructure ──
    public ObservableCollection<ISeries> LatencySeries { get; } = new();
    public Axis[] LatencyXAxes { get; }
    public Axis[] LatencyYAxes { get; }

    public ObservableCollection<ISeries> TraceSeries { get; } = new();
    public Axis[] TraceXAxes { get; }
    public Axis[] TraceYAxes { get; }

    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    public IReadOnlyList<TargetPreset> Presets => TargetPresets.All;
    [ObservableProperty] private TargetPreset _selectedPreset = TargetPresets.Global;

    public HealthDiagnostic Health { get; } = new();

    [ObservableProperty] private string _newTargetHost = "";
    [ObservableProperty] private int _intervalSeconds = 1;
    [ObservableProperty] private int _windowSeconds = 60;
    [ObservableProperty] private int _traceIntervalSeconds = 60;
    public int[] WindowOptions { get; } = { 60, 300, 600, 900 };
    public int[] TraceIntervalOptions { get; } = { 30, 60, 120, 300, 600 };

    [ObservableProperty] private bool _isMonitoring;

    public NetworkSharedState(PingMonitorService pinger, TracerouteService tracer, TracerouteMonitorService traceMonitor, SpeedTestService speed, NetworkRepairService repair)
    {
        Pinger = pinger;
        Tracer = tracer;
        TraceMonitor = traceMonitor;
        Speed = speed;
        Repair = repair;

        Dispatcher = Application.Current?.Dispatcher;
        if (Dispatcher is not null)
        {
            FlushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(UiFlushIntervalMs)
            };
            FlushTimer.Tick += (_, _) => FlushPending();
        }

        LatencyXAxes = [BuildTimeAxis()];
        LatencyYAxes = [BuildValueAxis("Latency (ms)")];
        TraceXAxes = [BuildHopAxis()];
        TraceYAxes = [BuildValueAxis("Latency (ms)")];

        // Paint chart labels/legend/tooltip from the active theme and keep them in sync,
        // so axis text stays readable on the light presets (a hardcoded near-white was
        // invisible on a white background). The seam is unsubscribed in Dispose().
        ApplyChartTheme();
        ThemeService.Instance.ThemeChanged += ApplyChartTheme;

        Pinger.SampleReceived += OnSample;
        TraceMonitor.RouteCompleted += OnRouteCompleted;

        var gw = GatewayHelper.DetectDefaultGateway();
        if (!string.IsNullOrEmpty(gw))
            AddTarget("Gateway", gw, TargetRole.Gateway);

        ApplyPreset(TargetPresets.Global);
    }

    // ── Presets ──

    partial void OnSelectedPresetChanged(TargetPreset value) => ApplyPreset(value);

    public void ApplyPreset(TargetPreset preset)
    {
        var toRemove = Targets.Where(t => t.Role != TargetRole.Gateway && !t.IsCustom).ToList();
        foreach (var t in toRemove) RemoveTargetInternal(t);

        foreach (var (name, host) in preset.Targets)
        {
            var role = preset.Name switch
            {
                "Global" when host.EndsWith(".8") || host.EndsWith(".1") || host.EndsWith(".9") => TargetRole.PublicDns,
                "Global" => TargetRole.Generic,
                "CS2 Europe" => TargetRole.GameServer,
                "FACEIT Europe" => TargetRole.GameServer,
                "PUBG Europe" => TargetRole.GameServer,
                "Streaming" => TargetRole.Streaming,
                _ => TargetRole.Generic
            };
            AddTarget(name, host, role);
        }

        foreach (var t in Targets.Where(t => t.Host is "8.8.8.8" or "1.1.1.1" or "9.9.9.9"))
            t.Role = TargetRole.PublicDns;
    }

    // ── Target management ──

    public void AddTarget(string name, string host, TargetRole role = TargetRole.Generic, bool isCustom = false)
    {
        if (Targets.Any(t => t.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) return;

        var color = Palette[PaletteIndex++ % Palette.Length];
        var target = new PingTarget(name, host, color, isCustom, role);
        Targets.Add(target);

        var buffer = new BulkObservableCollection<DateTimePoint>();
        Buffers[host] = buffer;

        var skColor = SKColor.Parse(color.TrimStart('#')).WithAlpha(230);

        LatencySeries.Add(new LineSeries<DateTimePoint>
        {
            Name = $"{name} ({host})",
            Values = buffer,
            Fill = null,
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(skColor, 1),
            GeometryFill = new SolidColorPaint(skColor),
            LineSmoothness = 0,
            Stroke = new SolidColorPaint(skColor, 2),
            AnimationsSpeed = TimeSpan.Zero
        });

        var traceBuffer = new BulkObservableCollection<ObservablePoint>();
        TraceBuffers[host] = traceBuffer;
        TraceSeries.Add(new LineSeries<ObservablePoint>
        {
            Name = $"{name} ({host})",
            Values = traceBuffer,
            Fill = null,
            GeometrySize = 6,
            LineSmoothness = 0,
            Stroke = new SolidColorPaint(skColor, 2),
            GeometryStroke = new SolidColorPaint(skColor, 2),
            GeometryFill = new SolidColorPaint(SKColor.Parse("0B0D10")),
            AnimationsSpeed = TimeSpan.Zero
        });

        Pinger.AddOrUpdate(target);
        TraceMonitor.AddOrUpdate(target);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PingTarget.IsEnabled))
            {
                Pinger.AddOrUpdate(target);
                TraceMonitor.AddOrUpdate(target);
                if (!target.IsEnabled)
                {
                    if (Buffers.TryGetValue(target.Host, out var b)) b.Clear();
                    if (TraceBuffers.TryGetValue(target.Host, out var tb)) tb.Clear();
                    target.LastLatencyMs = null;
                    target.AverageMs = null;
                    target.JitterMs = null;
                    target.LossPercent = 0;
                    target.Status = "—";
                    UpdateHealth();
                }
            }
        };
        target.PropertyChanged += handler;
        _targetHandlers[host] = handler;
    }

    public void AddCustomTarget()
    {
        var host = (NewTargetHost ?? "").Trim();
        if (string.IsNullOrEmpty(host)) return;
        AddTarget(host, host, TargetRole.Generic, isCustom: true);
        NewTargetHost = "";
    }

    public void RemoveTarget(PingTarget? target)
    {
        if (target is null || !target.IsCustom) return;
        RemoveTargetInternal(target);
    }

    internal void RemoveTargetInternal(PingTarget target)
    {
        if (_targetHandlers.TryGetValue(target.Host, out var handler))
        {
            target.PropertyChanged -= handler;
            _targetHandlers.Remove(target.Host);
        }
        Targets.Remove(target);

        // CQ-M1: Dispose SkiaSharp paint objects attached to the series being removed.
        // Without this, SolidColorPaint instances (and their unmanaged SKPaint handles)
        // leak every time a target is removed.
        var idx = LatencySeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (idx >= 0)
        {
            DisposeSeries(LatencySeries[idx]);
            LatencySeries.RemoveAt(idx);
        }
        var tIdx = TraceSeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (tIdx >= 0)
        {
            DisposeSeries(TraceSeries[tIdx]);
            TraceSeries.RemoveAt(tIdx);
        }

        Buffers.TryRemove(target.Host, out _);
        TraceBuffers.TryRemove(target.Host, out _);
        LatestRoutes.Remove(target.Host);
        Pinger.Remove(target.Host);
        TraceMonitor.Remove(target.Host);
        RefreshHopTable();
    }

    /// <summary>
    /// A small, stable per-host vertical offset (±~0.4 ms) so overlapping lines on the
    /// ping chart don't sit exactly on top of each other. Derived from the host's hash
    /// so it stays constant across target add/remove (unlike a list index).
    /// The sign bit is masked off rather than using Math.Abs, because GetHashCode can
    /// return int.MinValue and Math.Abs(int.MinValue) throws OverflowException.
    /// Internal for unit testing.
    /// </summary>
    internal static double StableOffset(string host)
    {
        var stableIdx = (host.GetHashCode() & int.MaxValue) % 8;
        return ((stableIdx % 8) - 3.5) * 0.25;
    }

    /// <summary>Disposes paint resources attached to a chart series.</summary>
    private static void DisposeSeries(ISeries series)
    {
        if (series is LineSeries<DateTimePoint> line)
        {
            (line.Stroke as IDisposable)?.Dispose();
            (line.GeometryStroke as IDisposable)?.Dispose();
            (line.GeometryFill as IDisposable)?.Dispose();
            (line.Fill as IDisposable)?.Dispose();
        }
        else if (series is LineSeries<ObservablePoint> traceLine)
        {
            (traceLine.Stroke as IDisposable)?.Dispose();
            (traceLine.GeometryStroke as IDisposable)?.Dispose();
            (traceLine.GeometryFill as IDisposable)?.Dispose();
            (traceLine.Fill as IDisposable)?.Dispose();
        }
    }

    public void ClearHistory()
    {
        foreach (var buf in Buffers.Values) buf.Clear();
        foreach (var buf in TraceBuffers.Values) buf.Clear();
        LatestRoutes.Clear();
        TracerouteHops.Clear();
        foreach (var t in Targets)
        {
            t.LastLatencyMs = null;
            t.AverageMs = null;
            t.JitterMs = null;
            t.LossPercent = 0;
            t.Status = "—";
        }
        // Reset axis limits so the chart starts fresh
        LatencyXAxes[0].MinLimit = null;
        LatencyXAxes[0].MaxLimit = null;
        UpdateHealth();
    }

    // ── Monitoring control ──

    public void StartMonitoring()
    {
        Pinger.Interval = TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds));
        TraceMonitor.Interval = TimeSpan.FromSeconds(Math.Max(10, TraceIntervalSeconds));
        Pinger.Start();
        TraceMonitor.Start();
        FlushTimer?.Start();
        IsMonitoring = true;
    }

    public void StopMonitoring()
    {
        Pinger.Stop();
        TraceMonitor.Stop();
        FlushTimer?.Stop();
        FlushPending();
        IsMonitoring = false;
        // Release axis limits so the chart auto-fits to remaining data
        LatencyXAxes[0].MinLimit = null;
        LatencyXAxes[0].MaxLimit = null;
    }

    partial void OnIntervalSecondsChanged(int value)
        => Pinger.Interval = TimeSpan.FromSeconds(Math.Max(1, value));

    partial void OnWindowSecondsChanged(int value) => TrimAllBuffers();

    partial void OnTraceIntervalSecondsChanged(int value)
        => TraceMonitor.Interval = TimeSpan.FromSeconds(Math.Max(10, value));

    // ── Sample handling ──

    private void OnSample(PingSample sample)
    {
        Pending.Enqueue(sample);
        // When Dispatcher is null (unit tests / headless), flush immediately on the
        // calling thread. ObservableCollections are not bound to UI in that scenario,
        // so direct modification is safe.
        if (Dispatcher is null) FlushPending();
    }

    internal void FlushPending()
    {
        var touched = new HashSet<string>();
        while (Pending.TryDequeue(out var sample))
        {
            if (!Buffers.TryGetValue(sample.Host, out var buffer)) continue;
            var target = Targets.FirstOrDefault(t => t.Host == sample.Host);
            if (target is null) continue;
            if (!target.IsEnabled) continue;

            double? shown = sample.LatencyMs;
            if (shown.HasValue)
            {
                // CQ-M2: Stable offset (same fix as RecomputeStats).
                shown = shown.Value + StableOffset(target.Host);
            }

            buffer.Add(new DateTimePoint(sample.Timestamp.ToLocalTime(), shown));
            target.LastLatencyMs = sample.LatencyMs;
            target.Status = sample.LatencyMs.HasValue ? "OK" : sample.Status;
            touched.Add(sample.Host);
        }

        foreach (var host in touched.Where(h => Buffers.ContainsKey(h)))
        {
            var buffer = Buffers[host];
            TrimBuffer(buffer);
            var target = Targets.FirstOrDefault(t => t.Host == host);
            if (target is null) continue;
            RecomputeStats(target, buffer);
        }

        if (touched.Count > 0)
        {
            // Pin the X-axis to a fixed time window so the chart never visually
            // collapses when points are added/removed from the buffer.
            var now = DateTime.Now;
            LatencyXAxes[0].MinLimit = now.AddSeconds(-WindowSeconds).Ticks;
            LatencyXAxes[0].MaxLimit = now.Ticks;
            UpdateHealth();
        }
    }

    private void RecomputeStats(PingTarget target, BulkObservableCollection<DateTimePoint> buffer)
    {
        var total = buffer.Count;
        if (total == 0)
        {
            target.AverageMs = null;
            target.JitterMs = null;
            target.LossPercent = 0;
            return;
        }

        // CQ-M2: Use a stable offset derived from the target's host hash instead
        // of Targets.IndexOf. IndexOf shifts after target removal, causing all
        // remaining targets to jump visually on the chart.
        var offset = StableOffset(target.Host);

        // PERF-M2: Avoid LINQ allocations (this runs 32x/sec per target).
        // Single pass over buffer to compute sum and count.
        int successful = 0;
        double sum = 0;
        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer[i].Value.HasValue)
            {
                sum += buffer[i].Value!.Value - offset;
                successful++;
            }
        }

        // Collect recent samples for jitter (walk backwards, no allocation beyond the list).
        var recent = new List<double>(JitterSampleWindow);
        for (int i = buffer.Count - 1; i >= 0 && recent.Count < JitterSampleWindow; i--)
        {
            if (buffer[i].Value.HasValue) recent.Add(buffer[i].Value!.Value - offset);
        }

        target.AverageMs = successful > 0 ? Math.Round(sum / successful, 1) : null;
        target.LossPercent = Math.Round(100.0 * (total - successful) / total, 1);

        if (recent.Count >= 2)
        {
            double mean = 0;
            for (int i = 0; i < recent.Count; i++) mean += recent[i];
            mean /= recent.Count;
            double variance = 0;
            for (int i = 0; i < recent.Count; i++) variance += (recent[i] - mean) * (recent[i] - mean);
            variance /= recent.Count;
            target.JitterMs = Math.Round(Math.Sqrt(variance), 1);
        }
        else
        {
            target.JitterMs = null;
        }
    }

    internal void UpdateHealth()
    {
        var metrics = Targets.Select(t => new HealthAnalyzer.TargetMetric(
            t.Name, t.Role, t.AverageMs, t.JitterMs, t.LossPercent,
            Buffers.TryGetValue(t.Host, out var b) ? b.Count : 0));
        var diag = HealthAnalyzer.Analyze(metrics);

        Health.Verdict = diag.Verdict;
        Health.Headline = diag.Headline;
        Health.Detail = diag.Detail;
        Health.ColorHex = diag.ColorHex;
        Health.WorstLossPercent = diag.WorstLossPercent;
        Health.WorstJitterMs = diag.WorstJitterMs;
        Health.AveragePingMs = diag.AveragePingMs;
    }

    private void OnRouteCompleted(string host, IReadOnlyList<TracerouteHop> hops)
        => InvokeOnUi(() => ApplyRoute(host, hops));

    internal void ApplyRoute(string host, IReadOnlyList<TracerouteHop> hops)
    {
        LatestRoutes[host] = hops;
        if (TraceBuffers.TryGetValue(host, out var buffer))
        {
            buffer.ReplaceWith(hops.Select(h => new ObservablePoint(h.HopNumber, h.LatencyMs ?? 0)));
        }
        RefreshHopTable();
    }

    internal void RefreshHopTable()
    {
        var hops = Targets
            .Where(t => LatestRoutes.ContainsKey(t.Host))
            .SelectMany(t => LatestRoutes[t.Host]);
        TracerouteHops.ReplaceWith(hops);
    }

    internal void TrimBuffer(BulkObservableCollection<DateTimePoint> buffer)
    {
        var cutoff = DateTime.Now - TimeSpan.FromSeconds(WindowSeconds);
        int removeCount = 0;
        while (removeCount < buffer.Count && buffer[removeCount].DateTime < cutoff)
            removeCount++;

        if (removeCount == 0) return;

        // PERF-M3: When removing many items from the front, repeated RemoveAt(0)
        // is O(n*removeCount) because each removal shifts all remaining elements.
        // If we're removing more than 25% of the buffer, it's cheaper to snapshot
        // the items we want to keep, clear, and re-add them (O(n) total).
        if (removeCount > buffer.Count / 4)
        {
            var keep = new DateTimePoint[buffer.Count - removeCount];
            for (int i = 0; i < keep.Length; i++)
                keep[i] = buffer[removeCount + i];
            buffer.ReplaceWith(keep);
        }
        else
        {
            for (int i = 0; i < removeCount; i++)
                buffer.RemoveAt(0);
        }
    }

    internal void TrimAllBuffers()
    {
        foreach (var b in Buffers.Values) TrimBuffer(b);
    }

    internal void InvokeOnUi(Action action)
    {
        if (Dispatcher is null || Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ── Chart theming ──

    /// <summary>
    /// Repaints the chart labels, legend, and tooltip from the current app theme. Wired to
    /// <see cref="ThemeService.ThemeChanged"/> so switching to a light preset makes the axis
    /// text dark-on-light (readable) instead of the previous hardcoded near-white.
    /// </summary>
    private void ApplyChartTheme() => Helpers.ChartTheme.Apply(
        LegendTextPaint, TooltipTextPaint, TooltipBackgroundPaint,
        [.. LatencyXAxes, .. LatencyYAxes, .. TraceXAxes, .. TraceYAxes]);

    // ── Axis factories ──

    internal static Axis BuildTimeAxis() => new()
    {
        Labeler = v => new DateTime((long)v).ToString("HH:mm:ss"),
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    internal static Axis BuildValueAxis(string name) => new()
    {
        Name = name,
        MinLimit = 0,
        TextSize = 13,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        Labeler = v => $"{v:F0} ms",
        NameTextSize = 14,
        ForceStepToMin = false,
        MinStep = 1
    };

    internal static Axis BuildHopAxis() => new()
    {
        Name = "Hop",
        MinStep = 1,
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ThemeService.Instance.ThemeChanged -= ApplyChartTheme;
        Pinger.SampleReceived -= OnSample;
        TraceMonitor.RouteCompleted -= OnRouteCompleted;
        // Stop, do not Dispose: Pinger / TraceMonitor are DI singletons the container
        // owns and disposes on teardown. Stopping halts their monitoring loops without
        // double-freeing a dependency this type does not own.
        Pinger.Stop();
        TraceMonitor.Stop();
        FlushTimer?.Stop();

        // Unsubscribe target PropertyChanged handlers to prevent memory leaks
        foreach (var (host, handler) in _targetHandlers)
        {
            var target = Targets.FirstOrDefault(t => t.Host == host);
            if (target is not null)
                target.PropertyChanged -= handler;
        }
        _targetHandlers.Clear();

        // Dispose series paint objects (unmanaged SkiaSharp handles)
        foreach (var series in LatencySeries) DisposeSeries(series);
        foreach (var series in TraceSeries) DisposeSeries(series);

        // Dispose axis paint objects
        DisposeAxisPaints(LatencyXAxes);
        DisposeAxisPaints(LatencyYAxes);
        DisposeAxisPaints(TraceXAxes);
        DisposeAxisPaints(TraceYAxes);

        // Dispose class-level paint objects and their typefaces
        LegendTextPaint.SKTypeface?.Dispose();
        (LegendTextPaint as IDisposable)?.Dispose();
        (LegendBackgroundPaint as IDisposable)?.Dispose();
        (TooltipTextPaint as IDisposable)?.Dispose();
        (TooltipBackgroundPaint as IDisposable)?.Dispose();
    }

    private static void DisposeAxisPaints(Axis[] axes)
    {
        foreach (var axis in axes)
        {
            (axis.NamePaint as IDisposable)?.Dispose();
            (axis.LabelsPaint as IDisposable)?.Dispose();
            (axis.SeparatorsPaint as IDisposable)?.Dispose();
        }
    }
}
