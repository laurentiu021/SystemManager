// SysManager · BandwidthMonitorViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
public class BandwidthMonitorViewModelTests
{
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

    private static BandwidthMonitorViewModel NewVm(
        Func<IBandwidthMonitorService>? connFactory = null,
        Func<IBandwidthMonitorService>? etwFactory = null)
    {
        var history = new BandwidthHistoryService();
        var vm = new BandwidthMonitorViewModel(
            history,
            connFactory ?? (() => new FakeSource(BandwidthMode.Connections, available: true)),
            etwFactory);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
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

        vm.TotalDownBytesPerSec = 2_000_000; // 16 Mbps > 10
        // Setting the threshold re-evaluates; force a re-evaluate by nudging the threshold setter.
        vm.AlertThresholdMbps = 10;
        Assert.True(vm.HasAlert);
        Assert.Contains("exceeded", vm.AlertMessage, StringComparison.OrdinalIgnoreCase);

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
}
