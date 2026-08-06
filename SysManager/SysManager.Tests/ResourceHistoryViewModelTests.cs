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
        // invariant (the series stay coherent, nothing escapes) rather than reproducing the observer
        // corruption. The gate is justified by the proven CI failure on the identical sibling.
        var now = DateTime.Now;
        using var service = SeededService(
            new ResourceSample(now.AddMinutes(-30), 10, 20, 30, 40, 50),
            new ResourceSample(now.AddMinutes(-20), 15, 25, 35, 45, 55),
            new ResourceSample(now.AddMinutes(-10), 20, 30, 40, 50, 60));
        using var vm = new ResourceHistoryViewModel(service);
        await vm.InitializationComplete;

        // Rapid range assignment starts a reload from the changed-handler each time, bypassing the
        // command — that is what genuinely overlaps.
        for (int i = 0; i < 20; i++)
            vm.SelectedRange = vm.RangeOptions[i % vm.RangeOptions.Count];

        // Fire the command mid-flight: the second, independent entry point.
        var refresh = vm.ReloadCommand.ExecuteAsync(null);
        for (int i = 0; i < 20; i++)
            vm.SelectedRange = vm.RangeOptions[(i + 1) % vm.RangeOptions.Count];
        await refresh;

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
}
