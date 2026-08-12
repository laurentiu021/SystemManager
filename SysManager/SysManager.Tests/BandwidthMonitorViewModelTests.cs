// SysManager · BandwidthMonitorViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BandwidthMonitorViewModel"/>: mode selection + ETW fallback, the total-rate
/// display formatting, PID-keyed row reconciliation, and the threshold-alert derivation. The source
/// factories are injected so no live network stack or ETW session is touched; a fake source returns
/// deterministic snapshots.
/// </summary>
public class BandwidthMonitorViewModelTests : IDisposable
{
    private readonly string _dir;

    public BandwidthMonitorViewModelTests()
    {
        // Every view model gets a throwaway history directory, so no test reads or writes the
        // developer's real %LOCALAPPDATA%\SysManager\bandwidth-history.ndjson.
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerBandwidthVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    // A deterministic in-memory source that yields a fixed snapshot on demand.
    private sealed class FakeSource(BandwidthMode mode, bool available, BandwidthSnapshot? snapshot = null) : IBandwidthMonitorService
    {
        private readonly BandwidthSnapshot _snap = snapshot
            ?? new BandwidthSnapshot(mode, 0, 0, []);
        public BandwidthMode Mode => mode;
        public bool IsAvailable { get; private set; }
        public bool StartReturnsAvailable { get; init; } = available;
        public bool Start() { IsAvailable = StartReturnsAvailable; return StartReturnsAvailable; }
        public Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default) => Task.FromResult(_snap);
        public void Dispose() { }
    }

    private BandwidthMonitorViewModel NewVm(
        Func<IBandwidthMonitorService>? connFactory = null,
        Func<IBandwidthMonitorService>? etwFactory = null,
        BandwidthHistoryService? history = null)
    {
        var vm = new BandwidthMonitorViewModel(
            history ?? new BandwidthHistoryService(_dir),
            connFactory ?? (() => new FakeSource(BandwidthMode.Connections, available: true)),
            etwFactory);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>
    /// Writes samples to the temp history file the way the poll loop does.
    /// <para>Samples MUST be passed oldest-first. Both history services document that the file is
    /// "append-only and time-ordered", and <c>LoadAsync</c> relies on it to stop at the first line
    /// outside the requested window instead of parsing a 120k-line week. Seeding out of order
    /// silently truncates the load and reads as a range-filtering bug — the assert makes the
    /// precondition fail loudly at the seam instead.</para>
    /// </summary>
    private BandwidthHistoryService SeededHistory(params BandwidthSample[] samples)
    {
        for (int i = 1; i < samples.Length; i++)
            Assert.True(samples[i].Timestamp >= samples[i - 1].Timestamp,
                $"Seed samples must be oldest-first; index {i} goes backwards in time.");

        var history = new BandwidthHistoryService(_dir);
        foreach (var s in samples)
            history.AppendAsync(s).GetAwaiter().GetResult();
        return history;
    }

    [Fact]
    public void AfterInit_UsesConnectionMode_WhenNoEtwFactory()
    {
        var vm = NewVm();
        Assert.False(vm.PreciseMode);
        Assert.Contains("connection", vm.ModeDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TotalRateDisplays_FormatAsBitsPerSecond()
    {
        var vm = NewVm();
        vm.TotalDownBytesPerSec = 1_250_000; // 10 Mbps
        vm.TotalUpBytesPerSec = 125_000;     // 1 Mbps
        Assert.Equal("10.0 Mbps", vm.DownDisplay);
        Assert.Equal("1.0 Mbps", vm.UpDisplay);
    }

    [Fact]
    public void MergeInto_ReconcilesRowsByPid_InPlace()
    {
        var vm = NewVm();

        vm.MergeInto([
            new ProcessNetworkUsage { ProcessId = 1, ProcessName = "a.exe", ConnectionCount = 2 },
            new ProcessNetworkUsage { ProcessId = 2, ProcessName = "b.exe", ConnectionCount = 5 },
        ]);
        Assert.Equal(2, vm.Processes.Count);
        var firstRowInstance = vm.Processes.First(p => p.ProcessId == 1);

        // Second merge: PID 1 survives (same instance, updated count), PID 2 gone, PID 3 new.
        vm.MergeInto([
            new ProcessNetworkUsage { ProcessId = 1, ProcessName = "a.exe", ConnectionCount = 9 },
            new ProcessNetworkUsage { ProcessId = 3, ProcessName = "c.exe", ConnectionCount = 1 },
        ]);

        Assert.Equal(2, vm.Processes.Count);
        var survivor = vm.Processes.First(p => p.ProcessId == 1);
        Assert.Same(firstRowInstance, survivor);       // instance preserved (keeps icon, no flicker)
        Assert.Equal(9, survivor.ConnectionCount);      // volatile field refreshed
        Assert.DoesNotContain(vm.Processes, p => p.ProcessId == 2); // gone removed
        Assert.Contains(vm.Processes, p => p.ProcessId == 3);       // new added
    }

    [Fact]
    public void Threshold_RaisesAndClearsAlert()
    {
        var vm = NewVm();
        vm.AlertThresholdMbps = 10;

        // A rate over the threshold raises the alert (the totals-changed handler re-evaluates).
        vm.TotalDownBytesPerSec = 2_000_000; // 16 Mbps > 10
        Assert.True(vm.HasAlert);
        Assert.Contains("exceeded", vm.AlertMessage, StringComparison.OrdinalIgnoreCase);

        // Dropping back under the threshold clears it.
        vm.TotalDownBytesPerSec = 500_000; // 4 Mbps < 10
        Assert.False(vm.HasAlert);

        // Raise it again, then disabling the threshold (0) also clears the alert.
        vm.TotalDownBytesPerSec = 2_000_000;
        Assert.True(vm.HasAlert);
        vm.AlertThresholdMbps = 0; // disable
        Assert.False(vm.HasAlert);
    }

    [Fact]
    public void PreciseRequested_WhenNotElevated_DoesNotEnterPreciseMode()
    {
        // The ETW factory would report available, but a non-elevated process must not use it.
        // On the test agent AdminHelper.IsElevated() is almost always false; assert the guard holds
        // by checking we stayed in connection mode after requesting precise.
        var vm = NewVm(etwFactory: () => new FakeSource(BandwidthMode.PreciseEtw, available: true));

        if (!vm.IsElevated)
        {
            vm.PreciseRequested = true;
            Assert.False(vm.PreciseMode); // guard kept us on the safe source
        }
    }

    [Fact]
    public void PreciseRequested_FallsBackToConnections_WhenEtwCannotStart()
    {
        // Model an elevated-but-ETW-unavailable host: the ETW source's Start() returns false, so the
        // VM must fall back to the connection source rather than showing precise mode.
        var vm = NewVm(etwFactory: () => new FakeSource(BandwidthMode.PreciseEtw, available: false));

        vm.PreciseRequested = true;

        // Regardless of elevation, an unavailable ETW source can never flip PreciseMode on.
        Assert.False(vm.PreciseMode);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = NewVm();
        vm.Dispose();
        vm.Dispose(); // must not throw (double teardown via OnClosed + Application.Exit)
    }

    // ── Recorded-history ranges (regression) ──
    // The poll loop always wrote a sample every ~5s and pruned to 7 days, but nothing ever read the
    // file back: LoadAsync and Downsample had zero production callers, so the data was invisible and
    // the tab's own documented purpose ("draw the last hour/day/week") was unmet.

    [Fact]
    public void RangeOptions_StartOnLive_AndOfferStoredRanges()
    {
        var vm = NewVm();

        Assert.True(vm.SelectedRange.IsLive);
        Assert.False(vm.ShowingHistory);
        // The stored ranges the persisted file exists to serve.
        Assert.Contains(vm.RangeOptions, r => r.Range == TimeSpan.FromHours(1));
        Assert.Contains(vm.RangeOptions, r => r.Range == TimeSpan.FromDays(1));
        Assert.Contains(vm.RangeOptions, r => r.Range == TimeSpan.FromDays(BandwidthHistoryService.RetentionDays));
    }

    [Fact]
    public void NoStoredRangeExceedsTheRetentionWindow()
    {
        // Offering "last 30 days" while the service prunes at 7 would promise data that was deleted.
        var vm = NewVm();

        Assert.All(vm.RangeOptions,
            r => Assert.True(r.Range <= TimeSpan.FromDays(BandwidthHistoryService.RetentionDays)));
    }

    [Fact]
    public async Task SelectingAStoredRange_LoadsWhatThePollLoopRecorded()
    {
        // The end-to-end assertion for this bug: samples on disk must reach the chart. Before the
        // fix, LoadAsync had no callers, so this file was written for 7 days and never read.
        var now = DateTime.Now;
        var history = SeededHistory(
            new BandwidthSample(now.AddMinutes(-30), 1_048_576, 131_072),
            new BandwidthSample(now.AddMinutes(-25), 2_097_152, 262_144),
            new BandwidthSample(now.AddMinutes(-20), 524_288, 65_536));
        using var vm = NewVm(history: history);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.ShowingHistory);
        Assert.False(vm.HistoryIsEmpty);
        Assert.Contains("3 recorded sample(s)", vm.StatusMessage);
        Assert.NotEqual("", vm.HistorySummary);
    }

    [Fact]
    public async Task AStoredRangeWithNothingInIt_ReportsEmptyRatherThanBlank()
    {
        // A blank chart reads as "no traffic". The empty state has to say why it is empty.
        var now = DateTime.Now;
        var history = SeededHistory(new BandwidthSample(now.AddDays(-3), 1_048_576, 0));
        using var vm = NewVm(history: history);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.ShowingHistory);
        Assert.True(vm.HistoryIsEmpty);
        Assert.Equal("", vm.HistorySummary);
    }

    [Fact]
    public async Task AWiderRangeIncludesSamplesANarrowerOneExcludes()
    {
        // Proves the range is actually applied rather than the whole file being returned regardless.
        var now = DateTime.Now;
        var history = SeededHistory(
            new BandwidthSample(now.AddDays(-3), 1_048_576, 0),
            new BandwidthSample(now.AddMinutes(-10), 1_048_576, 0));
        using var vm = NewVm(history: history);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);
        Assert.Contains("1 recorded sample(s)", vm.StatusMessage);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromDays(BandwidthHistoryService.RetentionDays));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);
        Assert.Contains("2 recorded sample(s)", vm.StatusMessage);
    }

    [Fact]
    public async Task ConcurrentReloadsDoNotCorruptTheChartSeries()
    {
        // Regression pin for a flaky CI failure that took two attempts to diagnose correctly.
        //
        // Assigning SelectedRange fires ReloadHistoryAsync fire-and-forget from its changed-handler,
        // and the Refresh button fires the same command directly — so a click during a load, or two
        // quick range changes, runs two reloads at once. Both call ReplaceWith on buffers LiveCharts
        // is observing, and its CollectionDeepObserver updates a HashSet from the change notification;
        // a second thread arriving mid-notification corrupts it and throws either "Collection was
        // modified" or "Operations that change non-concurrent collections must have exclusive access".
        //
        // My first fix guarded the POLL LOOP against the reload, which was the wrong culprit: this
        // test never sets IsActive, so the poll loop never samples. Reloads racing EACH OTHER is the
        // actual mechanism, hence a gate around the whole method.
        var now = DateTime.Now;
        var history = SeededHistory(
            new BandwidthSample(now.AddDays(-3), 1_048_576, 0),
            new BandwidthSample(now.AddMinutes(-10), 2_097_152, 262_144));
        using var vm = NewVm(history: history);

        var hourly = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        var weekly = vm.RangeOptions.First(r => r.Range == TimeSpan.FromDays(BandwidthHistoryService.RetentionDays));

        // Force the overlap deliberately: fire many reloads without awaiting between them, alternating
        // the range so each one has real work to do.
        var inFlight = new List<Task>();
        for (int i = 0; i < 24; i++)
        {
            vm.SelectedRange = i % 2 == 0 ? hourly : weekly;           // the handler starts one
            inFlight.Add(vm.ReloadHistoryCommand.ExecuteAsync(null));   // and this starts another
        }

        // The assertion is that none of them threw — the corruption surfaced as an exception, not as
        // wrong data. Task.WhenAll rethrows the first failure.
        await Task.WhenAll(inFlight);

        // And the chart is still coherent afterwards: both series present and equally long.
        var down = (vm.ThroughputSeries[0].Values as System.Collections.IEnumerable)!.Cast<object>().Count();
        var up = (vm.ThroughputSeries[1].Values as System.Collections.IEnumerable)!.Cast<object>().Count();
        Assert.Equal(down, up);
    }

    [Fact]
    public async Task ARedundantRefreshOnTheSameRangeStillEndsOnThatRange()
    {
        // The gate drops nothing user-visible: whichever range is selected is what ends up shown, even
        // when a second reload arrives for it.
        var now = DateTime.Now;
        var history = SeededHistory(
            new BandwidthSample(now.AddDays(-3), 1_048_576, 0),
            new BandwidthSample(now.AddMinutes(-10), 1_048_576, 0));
        using var vm = NewVm(history: history);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromDays(BandwidthHistoryService.RetentionDays));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);
        await vm.ReloadHistoryCommand.ExecuteAsync(null);   // a redundant Refresh on the same range

        Assert.True(vm.ShowingHistory);
        Assert.Contains("2 recorded sample(s)", vm.StatusMessage);
    }

    [Fact]
    public async Task AStoredRangeIsDownsampledToTheChartCap()
    {
        // A week of 5-second samples is ~120k points. Handing that to LiveCharts would stall the UI,
        // so Downsample has to be on the path — not merely defined.
        var now = DateTime.Now;
        var samples = Enumerable.Range(0, 1200)
            .Select(i => new BandwidthSample(now.AddSeconds(-5 * (1200 - i)), 1_048_576, 0))
            .ToArray();
        var history = SeededHistory(samples);
        using var vm = NewVm(history: history);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        // The status line reports what was loaded; the series carries the downsampled points.
        var plotted = vm.ThroughputSeries[0].Values as System.Collections.IEnumerable;
        Assert.NotNull(plotted);
        Assert.True(plotted!.Cast<object>().Count() <= 400);
    }

    [Fact]
    public async Task ChartTitle_TracksTheSelectedRange()
    {
        var vm = NewVm();
        Assert.Contains("2 minutes", vm.ChartTitle);

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromDays(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        Assert.Contains("24 hours", vm.ChartTitle);
    }

    [Fact]
    public async Task SwitchingBackToLive_LeavesHistoryState()
    {
        var vm = NewVm();
        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        vm.SelectedRange = vm.RangeOptions.First(r => r.IsLive);
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.ShowingHistory);
        Assert.False(vm.HistoryIsEmpty);
        Assert.Equal("", vm.HistorySummary);
    }

    // ── BuildHistorySummary: rates integrated over gaps, not summed ──

    private static BandwidthSample Sample(int atSecond, double down, double up)
        => new(new DateTime(2026, 8, 4, 12, 0, 0).AddSeconds(atSecond), down, up);

    [Fact]
    public void BuildHistorySummary_WithNoSamples_IsEmpty()
        => Assert.Equal("", BandwidthMonitorViewModel.BuildHistorySummary([]));

    [Fact]
    public void BuildHistorySummary_WithOneSample_ReportsThePeakButNoVolume()
    {
        // A single rate reading spans no interval, so no bytes can be attributed to it.
        var summary = BandwidthMonitorViewModel.BuildHistorySummary([Sample(0, 1_000_000, 500_000)]);

        Assert.Contains("Downloaded 0 B", summary);
        Assert.Contains("Peak", summary);
    }

    [Fact]
    public void BuildHistorySummary_IntegratesRatesOverTheGapBetweenSamples()
    {
        // 1 MB/s held across two 5-second gaps = 10 MB, NOT the 3 MB a naive sum of rates would give.
        var samples = new[]
        {
            Sample(0, 1_048_576, 0),
            Sample(5, 1_048_576, 0),
            Sample(10, 1_048_576, 0),
        };

        var summary = BandwidthMonitorViewModel.BuildHistorySummary(samples);

        Assert.Contains("Downloaded 10.0 MB", summary);
    }

    [Fact]
    public void BuildHistorySummary_DoesNotInventTrafficAcrossAClosedTabGap()
    {
        // Samples are only written while the tab is open. An hour-long gap means we have no idea what
        // happened in between — crediting the last known rate across it would fabricate ~3.7 GB.
        var samples = new[]
        {
            Sample(0, 1_048_576, 0),
            Sample(3600, 1_048_576, 0),
        };

        var summary = BandwidthMonitorViewModel.BuildHistorySummary(samples);

        Assert.Contains("Downloaded 0 B", summary);
    }

    [Fact]
    public void BuildHistorySummary_ReportsThePeakOfEachDirectionSeparately()
    {
        var samples = new[]
        {
            Sample(0, 1_250_000, 125_000),   // 10 Mbps down, 1 Mbps up
            Sample(5, 125_000, 1_250_000),   // the reverse — each peak comes from a different sample
        };

        var summary = BandwidthMonitorViewModel.BuildHistorySummary(samples);

        Assert.Contains("Peak ↓ 10.0 Mbps", summary);
        Assert.Contains("↑ 10.0 Mbps", summary);
    }

    [Fact]
    public void BuildHistorySummary_IgnoresANonAdvancingTimestamp()
    {
        // Two samples at the same instant, or out of order, must not contribute negative volume.
        var samples = new[] { Sample(5, 1_048_576, 0), Sample(5, 1_048_576, 0), Sample(0, 1_048_576, 0) };

        var summary = BandwidthMonitorViewModel.BuildHistorySummary(samples);

        Assert.Contains("Downloaded 0 B", summary);
    }

    // ── Axis labels adapt to the range ──

    [Fact]
    public void FormatAxisTick_LiveRange_ShowsSeconds()
    {
        var ticks = new DateTime(2026, 8, 4, 13, 45, 30).Ticks;

        Assert.Equal("13:45:30", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.Zero));
    }

    [Fact]
    public void FormatAxisTick_HourlyRange_DropsSeconds()
    {
        var ticks = new DateTime(2026, 8, 4, 13, 45, 30).Ticks;

        Assert.Equal("13:45", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void FormatAxisTick_MultiDayRange_IncludesTheDate()
    {
        // Over a week, a bare clock time repeats seven times and hides which day a spike was on.
        var ticks = new DateTime(2026, 8, 4, 13, 45, 30).Ticks;

        Assert.Equal("08-04 13:45", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.FromDays(7)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FormatAxisTick_OutOfRangeTicks_YieldAnEmptyLabel(double ticks)
    {
        // LiveCharts probes beyond the data on an empty series; a DateTime ctor there would throw.
        Assert.Equal("", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.Zero));
        Assert.Equal("", BandwidthMonitorViewModel.FormatAxisTick(ticks, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void FormatAxisTick_AtTheMaximumTickValue_YieldsAnEmptyLabel()
        => Assert.Equal("", BandwidthMonitorViewModel.FormatAxisTick(DateTime.MaxValue.Ticks, TimeSpan.Zero));

    // ── The window-visibility poll gate ─────────────────────────────────────
    // Closing to the tray calls Hide(), which does NOT deselect the open tab — so before this the
    // selected tab's 1 Hz poll loop kept running for as long as the PC stayed on. Since "minimize
    // to tray" is the default, that was the common all-day path.
    //
    // These exercise MainWindowViewModel.ApplyPollGate rather than the shell view-model itself: the
    // shell cannot be constructed in the unit suite because About's constructor runs the startup
    // update check (a network call), which is why its own tests live in the integration project —
    // and CI only COMPILE-checks that project, so a regression pinned there would never run.

    private static NavItem ItemFor(object content) => new()
    {
        Id = "nav-test",
        Label = "Test",
        Glyph = "",
        ViewType = typeof(object),
        Content = content,
    };

    [Fact]
    public void HidingTheWindow_PausesTheTabsPollLoop()
    {
        var vm = NewVm();
        vm.IsActive = true;

        MainWindowViewModel.ApplyPollGate(ItemFor(vm), windowVisible: false);

        Assert.False(vm.IsActive);
    }

    [Fact]
    public void ShowingTheWindow_ResumesTheTabsPollLoop()
    {
        var vm = NewVm();
        vm.IsActive = false;

        MainWindowViewModel.ApplyPollGate(ItemFor(vm), windowVisible: true);

        Assert.True(vm.IsActive);
    }

    [Fact]
    public void TheGate_IgnoresATabWhoseViewModelWasNeverBuilt()
    {
        // Reading Content on a lazy NavItem would CONSTRUCT the view-model, undoing the lazy-startup
        // fix — a never-opened tab has nothing polling, so the gate must skip it entirely.
        int built = 0;
        var lazy = new NavItem
        {
            Id = "nav-lazy",
            Label = "Lazy",
            Glyph = "",
            ViewType = typeof(object),
            ContentFactory = () => { built++; return new object(); },
        };

        MainWindowViewModel.ApplyPollGate(lazy, windowVisible: false);
        MainWindowViewModel.ApplyPollGate(lazy, windowVisible: true);

        Assert.Equal(0, built);
        Assert.False(lazy.IsContentCreated);
    }

    [Fact]
    public void TheGate_OnANullSelection_DoesNotThrow()
    {
        // SelectedNav is nullable and is null while the shell is still wiring up.
        MainWindowViewModel.ApplyPollGate(null, windowVisible: false);
        MainWindowViewModel.ApplyPollGate(null, windowVisible: true);
    }

    [Fact]
    public void TheGate_OnATabWithNoPollLoop_IsANoOp()
    {
        // Most tabs are not in SetActive's switch because they have nothing to pause. Passing one
        // must be harmless, since the gate runs for whichever tab happens to be selected.
        MainWindowViewModel.ApplyPollGate(ItemFor(new object()), windowVisible: false);
        MainWindowViewModel.ApplyPollGate(ItemFor(new object()), windowVisible: true);
    }

    // ── Dispose during an in-flight poll ────────────────────────────────────
    // SampleAsync now genuinely yields (its work runs on a worker thread), so the window can close —
    // disposing the view model, its source, and every SkiaSharp paint — while a sample is still in
    // flight. The cancellation token only stops a sample from STARTING; one already running completes
    // and its continuation would resume on the torn-down view model, mutating the LiveCharts buffers
    // and repainting through disposed native paint handles. PollOnceAsync re-checks disposal after the
    // await to bail before touching any of that.

    // A source whose SampleAsync blocks until released, so a Dispose() can be interleaved deterministically
    // between the await starting and its continuation running — no sleeps, no wall-clock.
    private sealed class GatedSource : IBandwidthMonitorService
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public BandwidthMode Mode => BandwidthMode.Connections;
        public bool IsAvailable => true;
        public bool Start() => true;

        public Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default) => Task.Run(() =>
        {
            _entered.TrySetResult();                       // signal we are inside the sample
            _release.Wait(TimeSpan.FromSeconds(5));        // bounded, so a broken test fails instead of hanging CI
            return new BandwidthSnapshot(BandwidthMode.Connections, 123, 456,
                [new ProcessNetworkUsage { ProcessId = 1, ProcessName = "app.exe", DownBytesPerSec = 1 }]);
        }, ct);

        public void ReleaseSample() => _release.Set();
        public void Dispose() => _release.Set();   // Dispose must not deadlock a waiting sample
    }

    [Fact]
    public async Task DisposeDuringAnInFlightPoll_DoesNotTouchTheTornDownViewModel()
    {
        var gated = new GatedSource();
        var vm = NewVm(connFactory: () => gated);

        // Invoke the poll directly so the in-flight moment is controllable. PollOnceAsync is private —
        // it is the seam the crash lives on, and there is no public trigger that pauses mid-sample.
        var poll = typeof(BandwidthMonitorViewModel)
            .GetMethod("PollOnceAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pollTask = (Task)poll.Invoke(vm, [CancellationToken.None])!;

        // Wait until the sample is genuinely running, THEN dispose — this is the interleave the fix guards.
        await gated.Entered;
        vm.Dispose();
        gated.ReleaseSample();

        // The continuation must complete without throwing (no repaint through disposed SkiaSharp handles)
        // and must NOT have applied the snapshot to the disposed view model.
        var completed = await Task.WhenAny(pollTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pollTask, completed);
        await pollTask;   // re-throws anything the continuation threw

        Assert.Equal(0, vm.TotalDownBytesPerSec);   // snapshot's 123 was never applied
        Assert.Equal(0, vm.TotalUpBytesPerSec);     // snapshot's 456 was never applied
        Assert.Empty(vm.Processes);                 // MergeInto never ran
    }

    // ── Reload after teardown ──
    // The reload path is serialized by a gate that Dispose disposes. Entering or releasing a disposed
    // SemaphoreSlim throws — the release out of a finally block, which would turn a clean tab close into
    // an unhandled exception. Both tests go through the public command, i.e. the same route the Refresh
    // button and a range change take.

    [Fact]
    public async Task ReloadAfterDispose_IsANoOpRatherThanAThrow()
    {
        var now = DateTime.UtcNow;
        var history = SeededHistory(new BandwidthSample(now.AddMinutes(-10), 1_048_576, 131_072));
        var vm = NewVm(history: history);
        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));

        vm.Dispose();

        // No throw, and nothing repainted: the chart buffers' paints and typefaces are already released,
        // so a completed reload here would draw through disposed SkiaSharp handles.
        await vm.ReloadHistoryCommand.ExecuteAsync(null);
        Assert.False(vm.ShowingHistory);
    }

    [Fact]
    public async Task DoubleDispose_IsHarmless()
    {
        // MainWindowViewModel disposes every tab on shutdown, and a tab can also be disposed by its own
        // teardown — the second call must not throw on the already-disposed gate, paints or source.
        var vm = NewVm();
        await vm.ReloadHistoryCommand.ExecuteAsync(null);

        vm.Dispose();
        vm.Dispose();
    }
}
