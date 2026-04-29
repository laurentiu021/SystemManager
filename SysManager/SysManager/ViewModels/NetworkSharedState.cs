// SysManager · NetworkSharedState
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
public sealed partial class NetworkSharedState : ObservableObject
{
    private const int UiFlushIntervalMs = 250;
    private const int JitterSampleWindow = 20;

    internal static readonly string[] Palette =
    {
        "#4CC9F0", "#80FFDB", "#F72585", "#FFD166",
        "#B388FF", "#06D6A0", "#FF6B6B", "#F8961E",
    };

    internal readonly PingMonitorService Pinger = new();
    internal readonly TracerouteService Tracer = new();
    internal readonly TracerouteMonitorService TraceMonitor = new();
    internal readonly SpeedTestService Speed = new();
    internal readonly NetworkRepairService Repair = new(new PowerShellRunner());
    internal readonly Dispatcher? Dispatcher;
    internal readonly DispatcherTimer? FlushTimer;
    internal readonly ConcurrentQueue<PingSample> Pending = new();
    internal int PaletteIndex;

    internal readonly Dictionary<string, ObservableCollection<DateTimePoint>> Buffers = new();
    internal readonly Dictionary<string, ObservableCollection<ObservablePoint>> TraceBuffers = new();
    internal readonly Dictionary<string, IReadOnlyList<TracerouteHop>> LatestRoutes = new();
    private readonly Dictionary<string, PropertyChangedEventHandler> _targetHandlers = new();

    public ObservableCollection<PingTarget> Targets { get; } = new();
    public ObservableCollection<TracerouteHop> TracerouteHops { get; } = new();

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

    public NetworkSharedState()
    {
        Dispatcher = Application.Current?.Dispatcher;
        if (Dispatcher != null)
        {
            FlushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(UiFlushIntervalMs)
            };
            FlushTimer.Tick += (_, _) => FlushPending();
        }

        LatencyXAxes = new[] { BuildTimeAxis() };
        LatencyYAxes = new[] { BuildValueAxis("Latency (ms)") };
        TraceXAxes  = new[] { BuildHopAxis() };
        TraceYAxes  = new[] { BuildValueAxis("Latency (ms)") };

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

        foreach (var t in Targets)
        {
            if (t.Host is "8.8.8.8" or "1.1.1.1" or "9.9.9.9")
                t.Role = TargetRole.PublicDns;
        }
    }

    // ── Target management ──

    public void AddTarget(string name, string host, TargetRole role = TargetRole.Generic, bool isCustom = false)
    {
        if (Targets.Any(t => t.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) return;

        var color = Palette[PaletteIndex++ % Palette.Length];
        var target = new PingTarget(name, host, color, isCustom, role);
        Targets.Add(target);

        var buffer = new ObservableCollection<DateTimePoint>();
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

        var traceBuffer = new ObservableCollection<ObservablePoint>();
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
        if (target == null || !target.IsCustom) return;
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
        var idx = LatencySeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (idx >= 0) LatencySeries.RemoveAt(idx);
        var tIdx = TraceSeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (tIdx >= 0) TraceSeries.RemoveAt(tIdx);
        Buffers.Remove(target.Host);
        TraceBuffers.Remove(target.Host);
        LatestRoutes.Remove(target.Host);
        Pinger.Remove(target.Host);
        TraceMonitor.Remove(target.Host);
        RefreshHopTable();
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
        if (Dispatcher == null) FlushPending();
    }

    internal void FlushPending()
    {
        var touched = new HashSet<string>();
        while (Pending.TryDequeue(out var sample))
        {
            if (!Buffers.TryGetValue(sample.Host, out var buffer)) continue;
            var target = Targets.FirstOrDefault(t => t.Host == sample.Host);
            if (target == null) continue;
            if (!target.IsEnabled) continue;

            double? shown = sample.LatencyMs;
            if (shown.HasValue)
            {
                var idx = Targets.IndexOf(target);
                var offset = ((idx % 8) - 3.5) * 0.25;
                shown = shown.Value + offset;
            }

            buffer.Add(new DateTimePoint(sample.Timestamp.ToLocalTime(), shown));
            target.LastLatencyMs = sample.LatencyMs;
            target.Status = sample.LatencyMs.HasValue ? "OK" : sample.Status;
            touched.Add(sample.Host);
        }

        foreach (var host in touched)
        {
            if (!Buffers.TryGetValue(host, out var buffer)) continue;
            TrimBuffer(buffer);
            var target = Targets.FirstOrDefault(t => t.Host == host);
            if (target == null) continue;
            RecomputeStats(target, buffer);
        }

        if (touched.Count > 0) UpdateHealth();
    }

    private void RecomputeStats(PingTarget target, ObservableCollection<DateTimePoint> buffer)
    {
        var total = buffer.Count;
        if (total == 0)
        {
            target.AverageMs = null;
            target.JitterMs = null;
            target.LossPercent = 0;
            return;
        }

        var idx = Targets.IndexOf(target);
        var offset = ((idx % 8) - 3.5) * 0.25;

        var successful = 0;
        var sum = 0.0;
        foreach (var p in buffer)
        {
            if (!p.Value.HasValue) continue;
            var raw = p.Value.Value - offset;
            successful++;
            sum += raw;
        }
        var recent = new List<double>(JitterSampleWindow);
        for (int i = buffer.Count - 1; i >= 0 && recent.Count < JitterSampleWindow; i--)
        {
            if (buffer[i].Value.HasValue) recent.Add(buffer[i].Value!.Value - offset);
        }

        target.AverageMs = successful > 0 ? Math.Round(sum / successful, 1) : null;
        target.LossPercent = Math.Round(100.0 * (total - successful) / total, 1);

        if (recent.Count >= 2)
        {
            var mean = recent.Average();
            var variance = recent.Sum(v => (v - mean) * (v - mean)) / recent.Count;
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
            buffer.Clear();
            foreach (var h in hops)
                buffer.Add(new ObservablePoint(h.HopNumber, h.LatencyMs ?? 0));
        }
        RefreshHopTable();
    }

    internal void RefreshHopTable()
    {
        TracerouteHops.Clear();
        foreach (var target in Targets)
        {
            if (!LatestRoutes.TryGetValue(target.Host, out var hops)) continue;
            foreach (var h in hops) TracerouteHops.Add(h);
        }
    }

    internal void TrimBuffer(ObservableCollection<DateTimePoint> buffer)
    {
        var cutoff = DateTime.Now - TimeSpan.FromSeconds(WindowSeconds);
        while (buffer.Count > 0 && buffer[0].DateTime < cutoff)
            buffer.RemoveAt(0);
    }

    internal void TrimAllBuffers()
    {
        foreach (var b in Buffers.Values) TrimBuffer(b);
    }

    internal void InvokeOnUi(Action action)
    {
        if (Dispatcher == null || Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ── Axis factories ──

    internal static Axis BuildTimeAxis() => new()
    {
        Labeler = v => new DateTime((long)v).ToString("HH:mm:ss"),
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    internal static Axis BuildValueAxis(string name) => new()
    {
        Name = name,
        MinLimit = 0,
        TextSize = 13,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
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
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };
}
