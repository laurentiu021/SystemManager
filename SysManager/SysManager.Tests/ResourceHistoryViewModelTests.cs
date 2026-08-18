// SysManager · ResourceHistoryViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class ResourceHistoryViewModelTests
{
    private static ResourceSample Sample(double cpu, double ram, double? cpuTemp = null)
        => new(new DateTime(2026, 6, 29), cpu, ram, null, cpuTemp, null);

    [Fact]
    public void BuildSummary_EmptySamples_IsEmpty()
        => Assert.Equal("", ResourceHistoryViewModel.BuildSummary([]));

    [Fact]
    public void BuildSummary_ReportsCpuAndRamAveragesAndPeaks()
    {
        var samples = new[] { Sample(20, 40), Sample(60, 80) };
        var summary = ResourceHistoryViewModel.BuildSummary(samples);
        Assert.Contains("CPU avg 40%", summary);
        Assert.Contains("peak 60%", summary);
        Assert.Contains("RAM avg 60%", summary);
        Assert.Contains("peak 80%", summary);
    }

    [Fact]
    public void BuildSummary_WithoutTemps_OmitsTempSegment()
    {
        var summary = ResourceHistoryViewModel.BuildSummary([Sample(10, 10)]);
        Assert.DoesNotContain("temp", summary);
    }

    [Fact]
    public void BuildSummary_WithTemps_IncludesPeakTemp()
    {
        var samples = new[] { Sample(10, 10, cpuTemp: 55), Sample(10, 10, cpuTemp: 72) };
        var summary = ResourceHistoryViewModel.BuildSummary(samples);
        Assert.Contains("CPU temp peak 72°C", summary);
    }
}

/// <summary>
/// Tests that drive the real reload path, which needs a <see cref="ResourceHistoryService"/> pointed
/// at a temp directory — only possible since that service gained the injectable configDir seam.
/// </summary>
public class ResourceHistoryViewModelReloadTests : IDisposable
{
    private readonly string _dir;

    public ResourceHistoryViewModelReloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerResourceHistoryVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    private ResourceHistoryService SeededService(params ResourceSample[] samples)
    {
        File.WriteAllLines(
            Path.Combine(_dir, "resource-history.ndjson"),
            samples.Select(ResourceHistoryService.Serialize));
        return new ResourceHistoryService(
            new SystemInfoService(),
            // skipHardwareInit: the reload path reads no sensor; probing real hardware would make
            // this test depend on the machine it runs on.
            new TemperatureService(new DiskHealthService(), skipHardwareInit: true),
            _dir);
    }

    [Fact]
    public async Task ConcurrentReloadsDoNotCorruptTheChartSeries()
    {
        // Regression pin for the same defect CI proved on BandwidthMonitorViewModel, which this VM
        // still carried: THREE entry points can run ReloadAsync at once — the constructor's
        // InitializeAsync, the fire-and-forget in OnSelectedRangeChanged, and the Refresh command —
        // and each calls ReplaceWith on five buffers LiveCharts observes. Its CollectionDeepObserver
        // updates a HashSet from the change notification, so a second thread arriving mid-notification
        // corrupts it ("Operations that change non-concurrent collections must have exclusive access").
        //
        // WHAT THIS TEST CAN AND CANNOT PROVE: the corruption needs a RENDERED chart for LiveCharts to
        // attach its observer, which never happens in a headless test — so this asserts the reachable
        // invariant (the series stay coherent at rest, nothing escapes) rather than reproducing the
        // observer corruption. The gate is justified by the proven CI failure on the identical sibling.
        var now = DateTime.Now;
        using var service = SeededService(
            new ResourceSample(now.AddMinutes(-30), 10, 20, 30, 40, 50),
            new ResourceSample(now.AddMinutes(-20), 15, 25, 35, 45, 55),
            new ResourceSample(now.AddMinutes(-10), 20, 30, 40, 50, 60));
        using var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        // Force the overlap: each SelectedRange assignment starts a fire-and-forget reload from the
        // changed-handler, and the command on the same line starts a second, independent one — both
        // through the one gate. Collect every command task so ALL of them can be awaited.
        //
        // The "all series equal length" invariant only holds AT REST: a reload applies its five
        // ReplaceWith calls one at a time, and between two of them the lengths legitimately differ
        // (0 mid-clear, then 3). In the real app the reader is the LiveCharts observer on the SAME UI
        // thread as the reload, so it never observes that transient; a headless test on the thread
        // pool would, if it read while a fire-and-forget reload was still in flight. Awaiting only one
        // reload while 20 more were fired after it is exactly that bug — it reddened CI ~1-in-4700
        // ([0, 3]). So await every command task, then one final lone reload with nothing fired after
        // it: a guaranteed coherent rest state (proven: 297/300 torn reads before this, 0/300 after).
        var inFlight = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            vm.SelectedRange = vm.RangeOptions[i % vm.RangeOptions.Count];   // the fire-and-forget path
            inFlight.Add(vm.ReloadCommand.ExecuteAsync(null));              // the command path
        }
        await Task.WhenAll(inFlight);
        await vm.ReloadCommand.ExecuteAsync(null);   // final reload, nothing fired after it → at rest

        // All three usage series are rebuilt from the same downsampled points, so their lengths must
        // agree. A torn ReplaceWith is exactly what makes them diverge.
        var lengths = vm.UsageSeries
            .Select(s => ((System.Collections.IEnumerable)s.Values!).Cast<object>().Count())
            .Distinct()
            .ToList();
        Assert.Single(lengths);
    }

    [Fact]
    public async Task ARedundantRefreshOnTheSameRangeStillShowsThatRange()
    {
        // The gate drops nothing user-visible: a second reload for the already-selected range still
        // ends on that range, with its samples counted, and releases the progress bar.
        var now = DateTime.Now;
        using var service = SeededService(
            new ResourceSample(now.AddMinutes(-20), 10, 20, null, null, null),
            new ResourceSample(now.AddMinutes(-10), 20, 30, null, null, null));
        using var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        vm.SelectedRange = vm.RangeOptions.First(r => r.Range == TimeSpan.FromHours(1));
        await vm.ReloadCommand.ExecuteAsync(null);
        await vm.ReloadCommand.ExecuteAsync(null);   // a redundant Refresh on the same range

        Assert.Equal(2, vm.SampleCount);
        Assert.True(vm.HasData);
        Assert.False(vm.IsBusy);   // the gate released, so the progress bar cleared
    }

    [Fact]
    public async Task ReloadWithNoHistory_ReportsTheEmptyStateRatherThanABlankChart()
    {
        using var service = SeededService();   // no samples at all
        using var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        Assert.False(vm.HasData);
        Assert.Equal(0, vm.SampleCount);
        Assert.Contains("No history yet", vm.StatusMessage);
    }

    // ── Dispose during an in-flight reload ──────────────────────────────────
    // ReloadAsync awaits twice (the reload gate, then the history load) and then calls ReplaceWith on
    // five buffers LiveCharts observes — while Dispose releases every chart series, axis paint, the
    // SkiaSharp typeface AND the gate itself. Closing the window mid-reload therefore had three ways to
    // fail: a wait on a disposed semaphore, a Release on a disposed semaphore out of a finally block,
    // and a repaint through disposed native handles. Reachable from all three entry points (the tab's
    // own init, a range switch, the Refresh button). Same class as the crash fixed in
    // BandwidthMonitorViewModel (v1.63.1).

    [Fact]
    public async Task DisposeDuringAnInFlightReload_DoesNotThrow()
    {
        var now = DateTime.Now;
        using var service = SeededService(
            new ResourceSample(now.AddMinutes(-20), 10, 20, 30, 40, 50),
            new ResourceSample(now.AddMinutes(-10), 15, 25, 35, 45, 55));
        var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        // Start a reload and dispose while it is still in flight. Not awaited first, deliberately: the
        // point is for teardown to land between the awaits.
        var reload = vm.ReloadCommand.ExecuteAsync(null);
        vm.Dispose();

        // Must complete without throwing — an ObjectDisposedException out of the finally, or a repaint
        // through a disposed paint, is exactly what the guards prevent.
        await reload;
    }

    [Fact]
    public async Task ReloadAfterDispose_IsANoOpRatherThanAThrow()
    {
        // The range ComboBox and the Refresh command can both fire during teardown, so a reload STARTED
        // after Dispose must bail rather than enter the disposed gate.
        var now = DateTime.Now;
        using var service = SeededService(new ResourceSample(now.AddMinutes(-5), 10, 20, 30, 40, 50));
        var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        vm.Dispose();

        await vm.ReloadCommand.ExecuteAsync(null);   // must not throw
        Assert.False(vm.IsBusy);                     // and must not leave the progress bar stuck on
    }

    [Fact]
    public async Task DoubleDispose_IsHarmless()
    {
        // MainWindowViewModel disposes tabs on shutdown, and a tab can also be disposed by its own
        // teardown path — the second call must not throw on the already-disposed gate or paints.
        using var service = SeededService();
        var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        vm.Dispose();
        vm.Dispose();
    }
}
