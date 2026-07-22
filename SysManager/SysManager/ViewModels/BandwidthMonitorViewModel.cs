// SysManager · BandwidthMonitorViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Serilog;
using SkiaSharp;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Bandwidth Monitor tab (Monitor group). Shows machine-wide download/upload speed with a live
/// history graph, plus a per-process list of who's using the network. Two measurement modes:
/// <list type="bullet">
/// <item><b>Connections (default, no admin):</b> accurate total throughput + per-app attribution
/// by active TCP/UDP connections. Works for everyone with zero friction.</item>
/// <item><b>Precise (ETW, admin):</b> true per-app download/upload rates and session totals from a
/// kernel trace. Offered only when the app is already elevated; falls back automatically if the
/// kernel session can't start.</item>
/// </list>
/// The poll loop runs only while the tab is visible (<see cref="IsActive"/>), mirroring
/// <see cref="ProcessManagerViewModel"/> / <see cref="AudioMixerViewModel"/>. Strictly local and
/// read-only — nothing is changed on the system and nothing leaves the machine.
/// </summary>
public sealed partial class BandwidthMonitorViewModel : ViewModelBase
{
    private const int PollIntervalMs = 1000;
    // Cap the top-consumers list so a machine with hundreds of connections stays readable and cheap.
    private const int MaxRows = 40;
    // Rolling live-chart window: 120 points at ~1s = the last ~2 minutes of throughput.
    private const int LiveChartPoints = 120;

    private readonly BandwidthHistoryService _history;
    private readonly Func<IBandwidthMonitorService> _connectionSourceFactory;
    private readonly Func<IBandwidthMonitorService>? _etwSourceFactory;

    private IBandwidthMonitorService? _source;
    private CancellationTokenSource? _pollCts;
    private DateTime _lastHistoryWrite = DateTime.MinValue;

    public BulkObservableCollection<ProcessNetworkUsage> Processes { get; } = new();

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _hasProcesses;
    [ObservableProperty] private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownDisplay))]
    private double _totalDownBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpDisplay))]
    private double _totalUpBytesPerSec;

    /// <summary>True when precise per-app rates (ETW) are active; false in connection mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private bool _preciseMode;

    /// <summary>User's opt-in for precise mode. Only honored when elevated; toggling re-inits the source.</summary>
    [ObservableProperty] private bool _preciseRequested;

    /// <summary>Alert threshold in Mbps for total throughput; 0 disables the alert.</summary>
    [ObservableProperty] private double _alertThresholdMbps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert))]
    private string _alertMessage = "";

    public bool HasAlert => AlertMessage.Length > 0;

    public string DownDisplay => BandwidthFormat.FormatRate(TotalDownBytesPerSec);
    public string UpDisplay => BandwidthFormat.FormatRate(TotalUpBytesPerSec);

    public string ModeDescription => PreciseMode
        ? "Precise per-app rates (administrator, live kernel trace)."
        : "Per-app activity by connection. Enable precise rates (needs administrator) for exact per-app speeds.";

    // ── Live throughput chart (rolling window, fed each poll) ─────────────
    public ObservableCollection<ISeries> ThroughputSeries { get; } = new();
    public Axis[] ThroughputXAxes { get; }
    public Axis[] ThroughputYAxes { get; }

    public SolidColorPaint LegendTextPaint { get; } = new(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
    public SolidColorPaint LegendBackgroundPaint { get; } = new(SKColors.Transparent);
    public SolidColorPaint TooltipTextPaint { get; } = new(SKColor.Parse("E6E9EE"));
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(SKColor.Parse("1C2230"));

    private readonly BulkObservableCollection<DateTimePoint> _downBuffer = new();
    private readonly BulkObservableCollection<DateTimePoint> _upBuffer = new();

    /// <summary>Production constructor — safe source always available; ETW source built on demand when elevated.</summary>
    public BandwidthMonitorViewModel(BandwidthHistoryService history)
        : this(history, () => new ConnectionBandwidthSource(), () => new EtwBandwidthSource())
    {
    }

    /// <summary>
    /// Test/seam constructor. The source factories are injected so unit tests can substitute a
    /// deterministic source without a live network stack or ETW. <paramref name="etwSourceFactory"/>
    /// may be null to model a build/host with no precise mode available.
    /// </summary>
    public BandwidthMonitorViewModel(
        BandwidthHistoryService history,
        Func<IBandwidthMonitorService> connectionSourceFactory,
        Func<IBandwidthMonitorService>? etwSourceFactory)
    {
        _history = history;
        _connectionSourceFactory = connectionSourceFactory;
        _etwSourceFactory = etwSourceFactory;
        IsElevated = AdminHelper.IsElevated();

        ThroughputXAxes = [BuildTimeAxis()];
        ThroughputYAxes = [BuildRateAxis()];
        ApplyChartTheme();
        ThemeService.Instance.ThemeChanged += ApplyChartTheme;
        // Download filled, upload as a line — one glance shows the split. Rates are stored in
        // bytes/sec and the Y axis labels them as bits/sec (Mbps), matching the stat tiles.
        ThroughputSeries.Add(BuildArea("Download", "#60A5FA", _downBuffer));
        ThroughputSeries.Add(BuildLine("Upload", "#A78BFA", _upBuffer));

        StatusMessage = "Starting network monitor…";
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        await _history.PruneAsync(ct).ConfigureAwait(true);
        StartSource();

        if (_pollCts is null || ct.IsCancellationRequested) return; // disposed during init
        _ = PollLoopAsync(ct);
    }

    /// <summary>
    /// (Re)creates the active source based on the current elevation + opt-in. Precise mode is used
    /// only when elevated, requested, and a factory exists AND the ETW session actually starts;
    /// otherwise the safe connection source is used. Disposes any previous source first.
    /// </summary>
    private void StartSource()
    {
        _source?.Dispose();
        _source = null;
        PreciseMode = false;

        if (PreciseRequested && IsElevated && _etwSourceFactory is not null)
        {
            var etw = _etwSourceFactory();
            if (etw.Start() && etw.IsAvailable)
            {
                _source = etw;
                PreciseMode = true;
                StatusMessage = "Precise per-app monitoring active.";
                return;
            }
            // ETW couldn't start — fall back cleanly.
            etw.Dispose();
            Log.Debug("Bandwidth: precise mode requested but ETW unavailable; using connection mode");
        }

        var safe = _connectionSourceFactory();
        safe.Start();
        _source = safe;
        StatusMessage = "Monitoring network activity.";
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(true);
                if (!IsActive || _source is null) continue;
                await PollOnceAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { break; }
            // A transient sampling fault must not kill the loop (matches ProcessManager/AudioMixer).
            catch (Exception ex) { Log.Debug("Bandwidth poll error: {Error}", ex.Message); }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (_source is null) return;
        var snap = await _source.SampleAsync(ct).ConfigureAwait(true);

        TotalDownBytesPerSec = snap.TotalDownBytesPerSec;
        TotalUpBytesPerSec = snap.TotalUpBytesPerSec;

        AppendToLiveChart(DateTime.Now, snap.TotalDownBytesPerSec, snap.TotalUpBytesPerSec);
        MergeInto(snap.Processes);
        HasProcesses = Processes.Count > 0;
        EvaluateAlert();

        // Feed the history graph at most once per ~5s so the file grows at a bounded rate even
        // though we poll every second (matches ResourceHistory's 10s-ish cadence intent).
        var now = DateTime.Now;
        if ((now - _lastHistoryWrite).TotalSeconds >= 5)
        {
            _lastHistoryWrite = now;
            await _history.AppendAsync(
                new BandwidthSample(now, snap.TotalDownBytesPerSec, snap.TotalUpBytesPerSec), ct)
                .ConfigureAwait(true);
        }

        StatusMessage = HasProcesses
            ? $"{Processes.Count} app{(Processes.Count == 1 ? "" : "s")} using the network."
            : "No network activity from user apps right now.";
    }

    /// <summary>
    /// Merges the snapshot rows into <see cref="Processes"/> keyed by PID: surviving processes keep
    /// their row instance (and thus their resolved icon) with the volatile fields refreshed, new
    /// processes are added (icon attached once), and gone processes removed. In-place reconciliation
    /// keeps the list from flickering and avoids re-extracting icons every second.
    /// </summary>
    internal void MergeInto(IReadOnlyList<ProcessNetworkUsage> snapshot)
    {
        var capped = snapshot.Count > MaxRows ? snapshot.Take(MaxRows).ToList() : snapshot;

        var existing = Processes.ToDictionary(r => r.ProcessId);
        var seen = new HashSet<int>(capped.Count);

        foreach (var row in capped)
        {
            seen.Add(row.ProcessId);
            if (existing.TryGetValue(row.ProcessId, out var current))
            {
                current.ConnectionCount = row.ConnectionCount;
                current.RemoteSummary = row.RemoteSummary;
                current.DownBytesPerSec = row.DownBytesPerSec;
                current.UpBytesPerSec = row.UpBytesPerSec;
                current.TotalDownBytes = row.TotalDownBytes;
                current.TotalUpBytes = row.TotalUpBytes;
            }
            else
            {
                row.Icon = IconExtractorService.GetProcessIcon(null, row.ProcessName);
                Processes.Add(row);
            }
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
            if (!seen.Contains(Processes[i].ProcessId))
                Processes.RemoveAt(i);

        SortInPlace();
    }

    private void SortInPlace()
    {
        var desired = Processes
            .OrderByDescending(r => PreciseMode ? r.DownBytesPerSec + r.UpBytesPerSec : r.ConnectionCount)
            .ThenBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < desired.Count; i++)
        {
            int current = Processes.IndexOf(desired[i]);
            if (current != i) Processes.Move(current, i);
        }
    }

    private void EvaluateAlert()
    {
        bool down = BandwidthFormat.ExceedsThresholdMbps(TotalDownBytesPerSec, AlertThresholdMbps);
        bool up = BandwidthFormat.ExceedsThresholdMbps(TotalUpBytesPerSec, AlertThresholdMbps);
        if (!down && !up) { AlertMessage = ""; return; }

        var which = down && up ? "Download and upload" : down ? "Download" : "Upload";
        AlertMessage = $"{which} exceeded {AlertThresholdMbps:0.#} Mbps (↓ {DownDisplay} · ↑ {UpDisplay}).";
    }

    partial void OnPreciseRequestedChanged(bool value)
    {
        if (value && !IsElevated)
        {
            StatusMessage = "Precise per-app rates need administrator. Use \"Run as administrator\" first.";
            return;
        }
        // Rebuild the source to switch modes; safe to call off the poll loop (the loop reads _source
        // atomically and simply picks up the new instance next tick).
        StartSource();
    }

    partial void OnAlertThresholdMbpsChanged(double value) => EvaluateAlert();

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    // ── Live chart plumbing (mirrors ResourceHistoryViewModel's idiom) ─────

    /// <summary>Appends one point to each rolling buffer and trims to the live window.</summary>
    private void AppendToLiveChart(DateTime at, double downBytesPerSec, double upBytesPerSec)
    {
        _downBuffer.Add(new DateTimePoint(at, downBytesPerSec));
        _upBuffer.Add(new DateTimePoint(at, upBytesPerSec));
        TrimBuffer(_downBuffer);
        TrimBuffer(_upBuffer);
    }

    private static void TrimBuffer(BulkObservableCollection<DateTimePoint> buffer)
    {
        while (buffer.Count > LiveChartPoints) buffer.RemoveAt(0);
    }

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

    private static LineSeries<DateTimePoint> BuildArea(string name, string hex, BulkObservableCollection<DateTimePoint> values)
    {
        var color = SKColor.Parse(hex.TrimStart('#')).WithAlpha(230);
        return new LineSeries<DateTimePoint>
        {
            Name = name,
            Values = values,
            Fill = new SolidColorPaint(color.WithAlpha(40)),
            GeometrySize = 0,
            LineSmoothness = 0.3,
            Stroke = new SolidColorPaint(color, 2),
            AnimationsSpeed = TimeSpan.Zero
        };
    }

    private static Axis BuildTimeAxis() => new()
    {
        Labeler = v => v > 0 && v < DateTime.MaxValue.Ticks ? new DateTime((long)v).ToString("HH:mm:ss") : "",
        TextSize = 12,
        NamePaint = new SolidColorPaint(SKColor.Parse("A3ADBF")),
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80))
    };

    private static Axis BuildRateAxis() => new()
    {
        Name = "Throughput",
        MinLimit = 0,
        TextSize = 13,
        NamePaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        LabelsPaint = new SolidColorPaint(SKColor.Parse("E6E9EE")) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") },
        SeparatorsPaint = new SolidColorPaint(SKColor.Parse("2A3244").WithAlpha(80)) { StrokeThickness = 1 },
        // Values are bytes/sec; label the axis in the same bits/sec units as the stat tiles.
        Labeler = v => BandwidthFormat.FormatRate(v),
        NameTextSize = 14
    };

    private void ApplyChartTheme() => ChartTheme.Apply(
        LegendTextPaint, TooltipTextPaint, TooltipBackgroundPaint,
        [.. ThroughputXAxes, .. ThroughputYAxes]);

    partial void OnIsActiveChanged(bool value)
    {
        // Only the poll loop honours IsActive (it skips sampling while hidden). Nothing to start/stop
        // here — but clear a stale alert when leaving so it doesn't linger on the hidden tab.
        if (!value) AlertMessage = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            _source?.Dispose();
            _source = null;

            ThemeService.Instance.ThemeChanged -= ApplyChartTheme;
            foreach (var s in ThroughputSeries) DisposeSeries(s);
            DisposeAxisPaints(ThroughputXAxes);
            DisposeAxisPaints(ThroughputYAxes);
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
            (axis.NamePaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.LabelsPaint as SolidColorPaint)?.SKTypeface?.Dispose();
            (axis.NamePaint as IDisposable)?.Dispose();
            (axis.LabelsPaint as IDisposable)?.Dispose();
            (axis.SeparatorsPaint as IDisposable)?.Dispose();
        }
    }
}
