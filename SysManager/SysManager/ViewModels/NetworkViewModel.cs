// SysManager · NetworkViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// Orchestrates the Network tab: live ping grid, overlaid latency chart,
/// auto-traceroute, HTTP/Ookla speed tests and a health verdict ("is it me
/// or is it the server?").
///
/// Performance notes:
///  - Samples arrive on the ping pump thread; they are queued lock-free and
///    drained to the UI at 4Hz via DispatcherTimer. Prevents UI congestion.
///  - Animations and curve smoothing are off on the live series — only
///    straight segments are redrawn per frame.
/// </summary>
public partial class NetworkViewModel : ViewModelBase
{
    private const int UiFlushIntervalMs = 250;
    private const int JitterSampleWindow = 20; // last N samples for jitter/stats

    private static readonly string[] Palette =
    {
        "#4CC9F0", "#80FFDB", "#F72585", "#FFD166",
        "#B388FF", "#06D6A0", "#FF6B6B", "#F8961E",
    };

    private readonly PingMonitorService _pinger = new();
    private readonly TracerouteService _tracer = new();
    private readonly TracerouteMonitorService _traceMonitor = new();
    private readonly SpeedTestService _speed = new();
    private readonly NetworkRepairService _repair = new(new PowerShellRunner());
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _flushTimer;
    private readonly ConcurrentQueue<PingSample> _pending = new();
    private CancellationTokenSource? _traceCts;
    private CancellationTokenSource? _speedCts;
    private int _paletteIndex;

    // Chart/trace buffers keyed by host.
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _buffers = new();
    private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _traceBuffers = new();
    private readonly Dictionary<string, IReadOnlyList<TracerouteHop>> _latestRoutes = new();

    // ---------- Bindable state ----------

    public ObservableCollection<PingTarget> Targets { get; } = new();
    public ObservableCollection<TracerouteHop> TracerouteHops { get; } = new();

    public ObservableCollection<ISeries> LatencySeries { get; } = new();
    public Axis[] LatencyXAxes { get; }
    public Axis[] LatencyYAxes { get; }

    public ObservableCollection<ISeries> TraceSeries { get; } = new();
    public Axis[] TraceXAxes { get; }
    public Axis[] TraceYAxes { get; }

    // Shared paints so chart legends are visible on dark backgrounds.
    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    public IReadOnlyList<TargetPreset> Presets => TargetPresets.All;
    [ObservableProperty] private TargetPreset _selectedPreset = TargetPresets.Global;

    public HealthDiagnostic Health { get; } = new();

    [ObservableProperty] private string _newTargetHost = "";
    [ObservableProperty] private string _traceHost = "8.8.8.8";

    [ObservableProperty] private int _intervalSeconds = 1;
    [ObservableProperty] private int _windowSeconds = 60;
    [ObservableProperty] private int _traceIntervalSeconds = 60;
    public int[] WindowOptions { get; } = { 60, 300, 600, 900 };
    public int[] TraceIntervalOptions { get; } = { 30, 60, 120, 300, 600 };

    [ObservableProperty] private bool _isMonitoring;

    /// <summary>
    /// Index of the selected sub-tab (0=Ping, 1=Traceroute, 2=Speed).
    /// When the user returns to the Ping tab, we nudge the chart series
    /// so LiveCharts2 re-renders the latest data (#153).
    /// </summary>
    [ObservableProperty] private int _selectedSubTab;

    partial void OnSelectedSubTabChanged(int value)
    {
        if (value == 0 && IsMonitoring)
        {
            // Force LiveCharts2 to re-evaluate the series by flushing any
            // pending samples and triggering a collection change notification.
            FlushPending();
            foreach (var series in LatencySeries)
            {
                if (series is LineSeries<DateTimePoint> line)
                    line.Values = line.Values; // identity assignment triggers change
            }
        }
    }

    [ObservableProperty] private SpeedTestResult? _httpResult;
    [ObservableProperty] private SpeedTestResult? _ooklaResult;
    [ObservableProperty] private int _speedProgress;
    [ObservableProperty] private string _speedStatus = "";
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private bool _isHttpTesting;
    [ObservableProperty] private bool _isOoklaTesting;

    [ObservableProperty] private bool _isTracing;
    [ObservableProperty] private string _traceStatus = "";

    // ---------- Repair state ----------
    [ObservableProperty] private bool _isRepairing;
    [ObservableProperty] private string _repairStatus = "";
    [ObservableProperty] private bool _repairNeedsReboot;

    public NetworkViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher;
        if (_dispatcher != null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(UiFlushIntervalMs)
            };
            _flushTimer.Tick += (_, _) => FlushPending();
        }

        LatencyXAxes = new[] { BuildTimeAxis() };
        LatencyYAxes = new[] { BuildValueAxis("Latency (ms)") };
        TraceXAxes  = new[] { BuildHopAxis() };
        TraceYAxes  = new[] { BuildValueAxis("Latency (ms)") };

        _pinger.SampleReceived += OnSample;
        _traceMonitor.RouteCompleted += OnRouteCompleted;

        // Always seed the local gateway first so the diagnostic can reason about it.
        var gw = GatewayHelper.DetectDefaultGateway();
        if (!string.IsNullOrEmpty(gw))
            AddTarget("Gateway", gw, TargetRole.Gateway);

        ApplyPreset(TargetPresets.Global);
    }

    // ---------- Presets ----------

    partial void OnSelectedPresetChanged(TargetPreset value) => ApplyPreset(value);

    /// <summary>
    /// Swap the active preset. Keeps the gateway and any custom targets.
    /// </summary>
    private void ApplyPreset(TargetPreset preset)
    {
        // Remove preset-provided targets; keep gateway + custom additions.
        var toRemove = Targets.Where(t => t.Role != TargetRole.Gateway && !t.IsCustom).ToList();
        foreach (var t in toRemove) RemoveTargetInternal(t);

        foreach (var (name, host) in preset.Targets)
        {
            var role = preset.Name switch
            {
                "Global" when host.EndsWith(".8") || host.EndsWith(".1") || host.EndsWith(".9") => TargetRole.PublicDns,
                "Global" => TargetRole.Generic,
                "CS2 Europe" => TargetRole.GameServer,
                "PUBG Europe" => TargetRole.GameServer,
                "Streaming" => TargetRole.Streaming,
                _ => TargetRole.Generic
            };
            AddTarget(name, host, role);
        }

        // Public DNS entries in the Global preset need explicit role tagging.
        foreach (var t in Targets)
        {
            if (t.Host is "8.8.8.8" or "1.1.1.1" or "9.9.9.9")
                t.Role = TargetRole.PublicDns;
        }

        StatusMessage = $"Preset: {preset.Name}";
    }

    // ---------- Target management ----------

    private void AddTarget(string name, string host, TargetRole role = TargetRole.Generic, bool isCustom = false)
    {
        if (Targets.Any(t => t.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) return;

        var color = Palette[_paletteIndex++ % Palette.Length];
        var target = new PingTarget(name, host, color, isCustom, role);
        Targets.Add(target);

        var buffer = new ObservableCollection<DateTimePoint>();
        _buffers[host] = buffer;

        var skColor = SKColor.Parse(color.TrimStart('#')).WithAlpha(230);

        LatencySeries.Add(new LineSeries<DateTimePoint>
        {
            Name = $"{name} ({host})",
            Values = buffer,
            Fill = null,
            // Small dots on every sample so you can still distinguish individual
            // points even when several lines overlap on the same latency.
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(skColor, 1),
            GeometryFill = new SolidColorPaint(skColor),
            LineSmoothness = 0,
            Stroke = new SolidColorPaint(skColor, 2),
            AnimationsSpeed = TimeSpan.Zero
        });

        var traceBuffer = new ObservableCollection<ObservablePoint>();
        _traceBuffers[host] = traceBuffer;
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

        _pinger.AddOrUpdate(target);
        _traceMonitor.AddOrUpdate(target);
        target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PingTarget.IsEnabled))
            {
                _pinger.AddOrUpdate(target);
                _traceMonitor.AddOrUpdate(target);
                if (!target.IsEnabled)
                {
                    if (_buffers.TryGetValue(target.Host, out var b)) b.Clear();
                    if (_traceBuffers.TryGetValue(target.Host, out var tb)) tb.Clear();
                    target.LastLatencyMs = null;
                    target.AverageMs = null;
                    target.JitterMs = null;
                    target.LossPercent = 0;
                    target.Status = "—";
                    UpdateHealth();
                }
            }
        };
    }

    // ---------- Commands ----------

    [RelayCommand]
    private void Start()
    {
        _pinger.Interval = TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds));
        _traceMonitor.Interval = TimeSpan.FromSeconds(Math.Max(10, TraceIntervalSeconds));
        _pinger.Start();
        _traceMonitor.Start();
        _flushTimer?.Start();
        IsMonitoring = true;
        StatusMessage = "Monitoring";
    }

    [RelayCommand]
    private void Stop()
    {
        _pinger.Stop();
        _traceMonitor.Stop();
        _flushTimer?.Stop();
        FlushPending();
        IsMonitoring = false;
        StatusMessage = "Stopped";
    }

    [RelayCommand]
    private void AddCustomTarget()
    {
        var host = (NewTargetHost ?? "").Trim();
        if (string.IsNullOrEmpty(host)) return;
        AddTarget(host, host, TargetRole.Generic, isCustom: true);
        NewTargetHost = "";
    }

    [RelayCommand]
    private void RemoveTarget(PingTarget? target)
    {
        if (target == null || !target.IsCustom) return;
        RemoveTargetInternal(target);
    }

    private void RemoveTargetInternal(PingTarget target)
    {
        Targets.Remove(target);
        var idx = LatencySeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (idx >= 0) LatencySeries.RemoveAt(idx);
        var tIdx = TraceSeries.ToList().FindIndex(s => s.Name?.Contains($"({target.Host})") == true);
        if (tIdx >= 0) TraceSeries.RemoveAt(tIdx);
        _buffers.Remove(target.Host);
        _traceBuffers.Remove(target.Host);
        _latestRoutes.Remove(target.Host);
        _pinger.Remove(target.Host);
        _traceMonitor.Remove(target.Host);
        RefreshHopTable();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        foreach (var buf in _buffers.Values) buf.Clear();
        foreach (var buf in _traceBuffers.Values) buf.Clear();
        _latestRoutes.Clear();
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

    partial void OnIntervalSecondsChanged(int value)
        => _pinger.Interval = TimeSpan.FromSeconds(Math.Max(1, value));

    partial void OnWindowSecondsChanged(int value) => TrimAllBuffers();

    partial void OnTraceIntervalSecondsChanged(int value)
        => _traceMonitor.Interval = TimeSpan.FromSeconds(Math.Max(10, value));

    // ---------- Traceroute (manual — on-demand for any host) ----------

    [RelayCommand]
    private async Task TraceAsync()
    {
        if (string.IsNullOrWhiteSpace(TraceHost)) return;
        IsTracing = true;
        TraceStatus = $"Tracing {TraceHost}…";

        _traceCts = new CancellationTokenSource();
        var collected = new List<TracerouteHop>();
        void OnHop(TracerouteHop hop)
        {
            collected.Add(hop);
            InvokeOnUi(() => TraceStatus = $"Tracing {TraceHost}… hop {hop.HopNumber}");
        }

        _tracer.HopCompleted += OnHop;
        try
        {
            await _tracer.RunAsync(TraceHost, _traceCts.Token);
            InvokeOnUi(() =>
            {
                ApplyRoute(TraceHost, collected);
                TraceStatus = $"Done — {collected.Count} hops";
            });
        }
        catch (OperationCanceledException) { TraceStatus = "Cancelled"; }
        catch (Exception ex) { TraceStatus = "Error: " + ex.Message; }
        finally
        {
            _tracer.HopCompleted -= OnHop;
            IsTracing = false;
        }
    }

    [RelayCommand]
    private void CancelTrace() => _traceCts?.Cancel();

    private void OnRouteCompleted(string host, IReadOnlyList<TracerouteHop> hops)
        => InvokeOnUi(() => ApplyRoute(host, hops));

    private void ApplyRoute(string host, IReadOnlyList<TracerouteHop> hops)
    {
        _latestRoutes[host] = hops;
        if (_traceBuffers.TryGetValue(host, out var buffer))
        {
            buffer.Clear();
            foreach (var h in hops)
                buffer.Add(new ObservablePoint(h.HopNumber, h.LatencyMs ?? 0));
        }
        RefreshHopTable();
    }

    private void RefreshHopTable()
    {
        TracerouteHops.Clear();
        foreach (var target in Targets)
        {
            if (!_latestRoutes.TryGetValue(target.Host, out var hops)) continue;
            foreach (var h in hops) TracerouteHops.Add(h);
        }
    }

    // ---------- Speed tests ----------

    [RelayCommand]
    private async Task RunHttpSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        IsHttpTesting = true;
        SpeedProgress = 0;
        SpeedStatus = "Starting HTTP speed test…";
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t => { SpeedProgress = t.p; SpeedStatus = t.m; });
        try { HttpResult = await _speed.RunHttpAsync(progress, _speedCts.Token); SpeedStatus = "HTTP done"; }
        catch (OperationCanceledException) { SpeedStatus = "Cancelled"; }
        catch (Exception ex) { SpeedStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsHttpTesting = false; }
    }

    [RelayCommand]
    private async Task RunOoklaSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        IsOoklaTesting = true;
        SpeedProgress = 0;
        SpeedStatus = "Starting Ookla speed test…";
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t => { SpeedProgress = t.p; SpeedStatus = t.m; });
        try { OoklaResult = await _speed.RunOoklaAsync(progress, _speedCts.Token); SpeedStatus = "Ookla done"; }
        catch (OperationCanceledException) { SpeedStatus = "Cancelled"; }
        catch (Exception ex) { SpeedStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsOoklaTesting = false; }
    }

    [RelayCommand]
    private void CancelSpeed() => _speedCts?.Cancel();

    // ---------- Network repair ----------

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        var result = MessageBox.Show(
            "Flush the DNS resolver cache?\n\nThis clears cached DNS lookups and forces fresh resolution. Safe and instant — no reboot needed.",
            "DNS Flush — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        await RunRepairAsync(() => _repair.FlushDnsAsync());
    }

    [RelayCommand]
    private async Task ResetWinsockAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            RepairStatus = "⚠ Winsock reset requires administrator privileges.";
            return;
        }

        var result = MessageBox.Show(
            "Reset the Winsock catalog?\n\nThis repairs corrupted network socket settings. A reboot is required for changes to take effect.",
            "Winsock Reset — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await RunRepairAsync(() => _repair.ResetWinsockAsync());
    }

    [RelayCommand]
    private async Task ResetTcpIpAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            RepairStatus = "⚠ TCP/IP reset requires administrator privileges.";
            return;
        }

        var result = MessageBox.Show(
            "Reset the TCP/IP stack?\n\nThis restores all TCP/IP settings to their defaults. A reboot is required for changes to take effect.",
            "TCP/IP Reset — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await RunRepairAsync(() => _repair.ResetTcpIpAsync());
    }

    private async Task RunRepairAsync(Func<Task<Models.NetworkRepairResult>> operation)
    {
        IsRepairing = true;
        RepairStatus = "Running…";
        try
        {
            var r = await operation();
            RepairStatus = r.Success
                ? $"✓ {r.ToolName} completed successfully."
                : $"✗ {r.ToolName} failed: {r.Output}";
            if (r.NeedsReboot && r.Success)
            {
                RepairNeedsReboot = true;
                RepairStatus += " Reboot required.";
            }
        }
        catch (OperationCanceledException) { RepairStatus = "Cancelled."; }
        catch (System.ComponentModel.Win32Exception ex) { RepairStatus = $"✗ Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { RepairStatus = $"✗ Error: {ex.Message}"; }
        finally { IsRepairing = false; }
    }

    // ---------- Sample handling ----------

    private void OnSample(PingSample sample)
    {
        _pending.Enqueue(sample);
        if (_dispatcher == null) FlushPending();
    }

    private void FlushPending()
    {
        var touched = new HashSet<string>();
        while (_pending.TryDequeue(out var sample))
        {
            if (!_buffers.TryGetValue(sample.Host, out var buffer)) continue;
            var target = Targets.FirstOrDefault(t => t.Host == sample.Host);
            if (target == null) continue;
            // Drop samples for disabled targets — a ping may have been in flight
            // when the user unchecked the target; we must not update its stats.
            if (!target.IsEnabled) continue;

            // Apply a tiny per-target visual offset so overlapping-latency
            // lines remain individually visible on the chart. Reported stats
            // (avg/jitter/loss) are computed from the raw value, not the shown one.
            double? shown = sample.LatencyMs;
            if (shown.HasValue)
            {
                var idx = Targets.IndexOf(target);
                // Alternating ±0.25ms fan-out, max ~1ms total spread.
                var offset = ((idx % 8) - 3.5) * 0.25;
                shown = shown.Value + offset;
            }

            buffer.Add(new DateTimePoint(sample.Timestamp.ToLocalTime(), shown));
            target.LastLatencyMs = sample.LatencyMs; // display raw, not offset
            target.Status = sample.LatencyMs.HasValue ? "OK" : sample.Status;
            touched.Add(sample.Host);
        }

        foreach (var host in touched)
        {
            if (!_buffers.TryGetValue(host, out var buffer)) continue;
            TrimBuffer(buffer);
            var target = Targets.FirstOrDefault(t => t.Host == host);
            if (target == null) continue;
            RecomputeStats(target, buffer);
        }

        if (touched.Count > 0) UpdateHealth();
    }

    /// <summary>
    /// Update avg/jitter/loss for a target based on its current buffer.
    /// Jitter = population stddev over the last N successful samples.
    /// The chart buffer contains a tiny per-target visual offset; we undo it
    /// here so the stats shown to the user reflect actual raw ping values.
    /// </summary>
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

        var recent = new List<double>(JitterSampleWindow);
        var successful = 0;
        var sum = 0.0;
        foreach (var p in buffer)
        {
            if (!p.Value.HasValue) continue;
            var raw = p.Value.Value - offset;
            successful++;
            sum += raw;
        }
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

    private void UpdateHealth()
    {
        var metrics = Targets.Select(t => new HealthAnalyzer.TargetMetric(
            t.Name, t.Role, t.AverageMs, t.JitterMs, t.LossPercent,
            _buffers.TryGetValue(t.Host, out var b) ? b.Count : 0));
        var diag = HealthAnalyzer.Analyze(metrics);

        Health.Verdict = diag.Verdict;
        Health.Headline = diag.Headline;
        Health.Detail = diag.Detail;
        Health.ColorHex = diag.ColorHex;
        Health.WorstLossPercent = diag.WorstLossPercent;
        Health.WorstJitterMs = diag.WorstJitterMs;
        Health.AveragePingMs = diag.AveragePingMs;
    }

    private void TrimBuffer(ObservableCollection<DateTimePoint> buffer)
    {
        var cutoff = DateTime.Now - TimeSpan.FromSeconds(WindowSeconds);
        while (buffer.Count > 0 && buffer[0].DateTime < cutoff)
            buffer.RemoveAt(0);
    }

    private void TrimAllBuffers()
    {
        foreach (var b in _buffers.Values) TrimBuffer(b);
    }

    private void InvokeOnUi(Action action)
    {
        if (_dispatcher == null || _dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ---------- Axis factories (kept together for style consistency) ----------

    private static Axis BuildTimeAxis() => new()
    {
        Labeler = v => new DateTime((long)v).ToString("HH:mm:ss"),
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    private static Axis BuildValueAxis(string name) => new()
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

    private static Axis BuildHopAxis() => new()
    {
        Name = "Hop",
        MinStep = 1,
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { FontFamily = "Segoe UI" },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };
}
