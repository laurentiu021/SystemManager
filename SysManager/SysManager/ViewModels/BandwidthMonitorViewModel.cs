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
    // A stored range is downsampled to this many points before plotting — same cap and reason as
    // ResourceHistoryViewModel: a chart reads fine at a few hundred points and costs CPU beyond that.
    private const int MaxHistoryPoints = 400;
    // How often a sample is persisted, in seconds. Bounds the file's growth (we poll every second).
    private const double HistoryWriteIntervalSeconds = 5;
    // Longest gap between two samples still treated as continuous monitoring, at 4× the write
    // cadence. Beyond it the tab was closed, so the interval is skipped rather than extrapolated.
    private const double MaxCreditedGapSeconds = HistoryWriteIntervalSeconds * 4;

    private readonly BandwidthHistoryService _history;
    private readonly Func<IBandwidthMonitorService> _connectionSourceFactory;
    private readonly Func<IBandwidthMonitorService>? _etwSourceFactory;

    private IBandwidthMonitorService? _source;
    private CancellationTokenSource? _pollCts;
    private DateTime _lastHistoryWrite = DateTime.MinValue;

    public BulkObservableCollection<ProcessNetworkUsage> Processes { get; } = new();

    /// <summary>
    /// Time ranges for the throughput chart. "Live" is the in-memory rolling window; the rest load
    /// the samples the poll loop has been persisting, capped at the service's 7-day retention.
    /// </summary>
    public IReadOnlyList<HistoryRange> RangeOptions { get; } =
    [
        new("Live (~2 min)", TimeSpan.Zero),
        new("Last hour", TimeSpan.FromHours(1)),
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days", TimeSpan.FromDays(BandwidthHistoryService.RetentionDays)),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartTitle))]
    private HistoryRange _selectedRange;

    /// <summary>True while a stored range is shown — the live poll then stops repainting the chart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartTitle))]
    private bool _showingHistory;

    /// <summary>Set when a stored range was selected but no samples fall inside it.</summary>
    [ObservableProperty] private bool _historyIsEmpty;

    /// <summary>Totals for the loaded range, e.g. "Downloaded 4.2 GB · Uploaded 380 MB" — empty in live mode.</summary>
    [ObservableProperty] private string _historySummary = "";

    public string ChartTitle => ShowingHistory
        ? $"Throughput — {SelectedRange.Label.ToLowerInvariant()}"
        : "Throughput (last ~2 minutes)";

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

    /// <summary>
    /// True while <see cref="ReloadHistoryAsync"/> owns the two chart buffers, so the poll loop must
    /// not append to them. Distinct from <see cref="ShowingHistory"/>, which is a UI-facing property
    /// set only once the load has landed: this is claimed BEFORE the load's first await, covering the
    /// whole span in which the buffers may be rebuilt. Both the poll loop and the reload run on the UI
    /// dispatcher in the app, but a test host has no dispatcher, so the two genuinely interleave and
    /// LiveCharts throws "Collection was modified" on a buffer mutated mid-notification.
    /// </summary>
    private bool _historyOwnsChart;

    /// <summary>
    /// Serializes <see cref="ReloadHistoryAsync"/> so two reloads never rebuild the chart buffers at
    /// once. See the comment there for why they overlap in the first place. Disposed with the VM.
    /// </summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

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
        _selectedRange = RangeOptions[0]; // Live — the tab's job is the current moment first.

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

        // Re-check AFTER the await, not just before it. SampleAsync now genuinely yields (its work runs
        // on a worker thread), so Dispose() — which cancels _pollCts and disposes _source plus every
        // SkiaSharp paint — can run while a sample is in flight. The cancellation token only prevents a
        // sample from STARTING; one already running completes and would resume here, writing bound
        // state, mutating the LiveCharts buffers and repainting through disposed native paint handles on
        // a torn-down view model (the window can close mid-poll — ShutdownMode is OnExplicitShutdown, so
        // the dispatcher keeps pumping this queued continuation). Same guard the init path already uses
        // after its awaits (see InitAsync). _source is nulled in Dispose, so this covers it directly.
        if (_source is null || _pollCts is null || ct.IsCancellationRequested) return;

        TotalDownBytesPerSec = snap.TotalDownBytesPerSec;
        TotalUpBytesPerSec = snap.TotalUpBytesPerSec;

        // Setting the totals above already re-evaluates the alert via their changed-handlers.
        // Keep filling the live buffers only while the live range is shown; a stored range owns the
        // chart until the user switches back, otherwise each poll would append a "now" point onto a
        // week-long series and squash it.
        //
        // Gated on _historyOwnsChart rather than ShowingHistory: that property is only set AFTER the
        // load's await completes, leaving a window in which this poll could Add to a buffer while
        // ReloadHistoryAsync was rebuilding it — LiveCharts observes those buffers and throws
        // "Collection was modified" when it sees one mutate mid-notification.
        if (!_historyOwnsChart)
            AppendToLiveChart(DateTime.Now, snap.TotalDownBytesPerSec, snap.TotalUpBytesPerSec);
        MergeInto(snap.Processes);
        HasProcesses = Processes.Count > 0;

        // Feed the history graph at most once per ~5s so the file grows at a bounded rate even
        // though we poll every second (matches ResourceHistory's 10s-ish cadence intent).
        var now = DateTime.Now;
        if ((now - _lastHistoryWrite).TotalSeconds >= HistoryWriteIntervalSeconds)
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

    partial void OnSelectedRangeChanged(HistoryRange value)
    {
        RefreshTimeAxisLabeler();
        _ = ReloadHistoryAsync();
    }

    /// <summary>
    /// Repaints the chart for the selected range. Live clears the buffers and hands them back to the
    /// poll loop; a stored range loads the persisted samples, downsamples them, and freezes the chart
    /// on that window.
    /// <para>This is what the sample file is for. The poll loop has always written a sample every ~5s
    /// (and pruned to a 7-day window), but nothing ever read it back, so the data was invisible —
    /// pure disk churn. The service's Load/Downsample already existed and were unit-tested; only the
    /// call was missing.</para>
    /// </summary>
    [RelayCommand]
    private async Task ReloadHistoryAsync()
    {
        // Serialized: two reloads must never rebuild the chart buffers at the same time. They overlap
        // easily — picking a range fires this from OnSelectedRangeChanged (fire-and-forget), and the
        // Refresh button fires the same command directly, so a click during a load, or two quick range
        // changes, gives two concurrent runs. Both then call ReplaceWith on collections LiveCharts is
        // observing, and its observer keeps a HashSet it updates from the change notification — a
        // second thread arriving mid-notification corrupts it ("Operations that change non-concurrent
        // collections must have exclusive access"), which is how CI caught this.
        //
        // A gate rather than a lock: this is an async path, so it must not block, and dropping an
        // overlapping reload is correct behaviour — the newest selection is what the user asked for
        // and the queued run would repaint with identical data anyway.
        //
        // Nothing to reload into once the tab is gone, and Dispose disposes the gate below — so entering
        // it after teardown throws instead of doing work, and completing a load would repaint chart
        // buffers whose paints and typefaces Dispose has already released.
        if (IsDisposed) return;

        try
        {
            await _reloadGate.WaitAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and the wait: the tab closed mid-reload. Nothing to
            // release, because the wait never succeeded.
            return;
        }

        try
        {
            var range = SelectedRange;
            if (range.IsLive)
            {
                _historyOwnsChart = false;
                ShowingHistory = false;
                HistoryIsEmpty = false;
                HistorySummary = "";
                // Start the live window from empty rather than showing stale points from before the
                // history detour; the poll loop refills it within a second.
                _downBuffer.Clear();
                _upBuffer.Clear();
                return;
            }

            // Claimed BEFORE the await, not after the load lands: the poll loop must stop touching the
            // buffers for the whole span in which this method may rebuild them, otherwise its Add
            // races ReplaceWith on a collection LiveCharts is observing.
            _historyOwnsChart = true;
            IsBusy = true;
            try
            {
                var loaded = await _history.LoadAsync(range.Range, _pollCts?.Token ?? default).ConfigureAwait(true);
                var points = BandwidthHistoryService.Downsample(loaded, MaxHistoryPoints);

                _downBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.DownBytesPerSec)));
                _upBuffer.ReplaceWith(points.Select(p => new DateTimePoint(p.Timestamp, p.UpBytesPerSec)));

                ShowingHistory = true;
                HistoryIsEmpty = loaded.Count == 0;
                HistorySummary = BuildHistorySummary(loaded);
                StatusMessage = loaded.Count > 0
                    ? $"Showing {loaded.Count} recorded sample(s) over {range.Label.ToLowerInvariant()}."
                    : "No history recorded for that range yet — samples are saved while this tab is open.";
            }
            catch (OperationCanceledException)
            {
                // Tab closed mid-load. Hand the buffers back, or a cancelled load would leave the live
                // chart permanently frozen the next time the tab is opened.
                _historyOwnsChart = false;
            }
            finally { IsBusy = false; }
        }
        finally
        {
            // Release only if the gate is still alive: Dispose can land while the load is in flight, and
            // releasing a disposed SemaphoreSlim throws — out of a finally block, which would replace a
            // clean teardown with an unhandled exception.
            if (!IsDisposed)
            {
                try { _reloadGate.Release(); }
                catch (ObjectDisposedException) { /* disposed mid-reload; nothing left to release */ }
            }
        }
    }

    /// <summary>
    /// Totals and peaks for a loaded range: how much moved and how fast it peaked. Pure so it is
    /// testable without WPF (mirrors <see cref="ResourceHistoryViewModel.BuildSummary"/>).
    /// <para>Each sample is a rate, not a volume, so the byte totals are integrated over the gaps
    /// between consecutive samples rather than summed — samples are ~5s apart while the tab is open
    /// but arbitrarily far apart across sessions, and summing rates would invent traffic for every
    /// minute the tab was closed.</para>
    /// </summary>
    internal static string BuildHistorySummary(IReadOnlyList<BandwidthSample> samples)
    {
        if (samples.Count == 0) return "";

        double downBytes = 0, upBytes = 0, peakDown = 0, peakUp = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            peakDown = Math.Max(peakDown, samples[i].DownBytesPerSec);
            peakUp = Math.Max(peakUp, samples[i].UpBytesPerSec);
            if (i == 0) continue;

            // Only credit a gap that plausibly belongs to one continuous stretch of monitoring. A
            // longer gap means the tab (or the app) was closed and we have no idea what happened.
            double gapSeconds = (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds;
            if (gapSeconds <= 0 || gapSeconds > MaxCreditedGapSeconds) continue;
            downBytes += samples[i].DownBytesPerSec * gapSeconds;
            upBytes += samples[i].UpBytesPerSec * gapSeconds;
        }

        return $"Downloaded {BandwidthFormat.FormatBytes((long)downBytes)} · " +
               $"Uploaded {BandwidthFormat.FormatBytes((long)upBytes)}   ·   " +
               $"Peak ↓ {BandwidthFormat.FormatRate(peakDown)} · ↑ {BandwidthFormat.FormatRate(peakUp)}";
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

    // Re-evaluate the alert whenever the totals change too, not only on a threshold edit — so the
    // banner always reflects the current rate (the poll loop also calls EvaluateAlert directly, but
    // this keeps the alert correct for any path that updates the totals).
    partial void OnTotalDownBytesPerSecChanged(double value) => EvaluateAlert();
    partial void OnTotalUpBytesPerSecChanged(double value) => EvaluateAlert();

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

    /// <summary>
    /// Formats an axis tick. The live window spans two minutes so it needs seconds; a stored range
    /// spans hours or days, where "HH:mm:ss" repeats the same label across a whole screen and hides
    /// which day a spike was on. <paramref name="range"/> at or below zero means the live window.
    /// </summary>
    internal static string FormatAxisTick(double ticks, TimeSpan range)
    {
        if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks) return "";
        var at = new DateTime((long)ticks);
        if (range <= TimeSpan.Zero) return at.ToString("HH:mm:ss");
        return range > TimeSpan.FromHours(24) ? at.ToString("MM-dd HH:mm") : at.ToString("HH:mm");
    }

    private void RefreshTimeAxisLabeler()
    {
        var range = SelectedRange.Range;
        ThroughputXAxes[0].Labeler = v => FormatAxisTick(v, range);
    }

    private static Axis BuildTimeAxis() => new()
    {
        Labeler = v => FormatAxisTick(v, TimeSpan.Zero),
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
            _reloadGate.Dispose();

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
