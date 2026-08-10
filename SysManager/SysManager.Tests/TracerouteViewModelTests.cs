// SysManager · TracerouteViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class TracerouteViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultTraceHost_Is8888()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.Equal("8.8.8.8", vm.TraceHost);
    }

    [Fact]
    public void IsTracing_DefaultFalse()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void IsAutoTraceRunning_DefaultFalse()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsAutoTraceRunning);
    }

    [Fact]
    public void CancelTraceCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        vm.CancelTraceCommand.Execute(null);
    }

    // ── The auto-trace monitor is SHARED with the Ping tab ──────────────────
    // TraceMonitor is one instance that NetworkSharedState.Start/StopMonitoring (driven by the Ping
    // tab) and TracerouteViewModel both start and stop. When the running flag was a local
    // [ObservableProperty] on this VM, the other tab's actions left it stale — the button lied about
    // whether traceroutes were running. These pin the shared flag as the single source of truth.

    private static NetworkSharedState NewShared() => new(
        new Services.PingMonitorService(), new Services.TracerouteService(),
        new Services.TracerouteMonitorService(), new Services.SpeedTestService(),
        new Services.NetworkRepairService(new Services.PowerShellRunner()));

    // NOTE ON SCOPE: these deliberately do NOT call NetworkSharedState.StartMonitoring(), even
    // though that is the exact call the Ping tab makes. StartMonitoring starts PingMonitorService,
    // whose pump sends a real ICMP echo on its FIRST tick (before any delay) to the targets the
    // constructor seeds — including the machine's actual gateway. A unit test must not emit live
    // network traffic: it would be non-deterministic, and on a workstation it can raise a firewall
    // prompt. What the fix changes is WHERE the flag lives, so these assert the flag contract
    // directly. That Start/StopMonitoring set it is verified by reading those two methods, and the
    // full cross-tab path belongs in the integration suite.

    [Fact]
    public void TheFlagIsNotStoredOnThisViewModel_ItReadsTheSharedState()
    {
        // The root cause was a duplicated [ObservableProperty] on this VM. If the property were
        // still local, writing the shared flag would leave the VM's value untouched — which is
        // exactly how Ping's Stop left the tab claiming "Stop auto-trace".
        using var shared = NewShared();
        using var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsAutoTraceRunning);

        shared.IsAutoTraceRunning = true;             // as Start/StopMonitoring does

        Assert.True(vm.IsAutoTraceRunning);           // stayed false before the fix

        shared.IsAutoTraceRunning = false;
        Assert.False(vm.IsAutoTraceRunning);
    }

    [Fact]
    public void StoppingAutoTraceOnThisTab_IsVisibleOnTheSharedState()
    {
        // The reverse direction: this tab's own Stop must be observable by anything reading the
        // shared flag, not just by this VM's local copy.
        using var shared = NewShared();
        using var vm = new TracerouteViewModel(shared);
        shared.IsAutoTraceRunning = true;

        vm.StopAutoTraceCommand.Execute(null);

        Assert.False(shared.IsAutoTraceRunning);
        Assert.False(vm.IsAutoTraceRunning);
    }

    [Fact]
    public void FlippingTheSharedFlag_RaisesPropertyChangedOnThisViewModel()
    {
        // The forwarding property is only useful if it notifies — otherwise the bound button keeps
        // rendering the old state even though the value changed.
        using var shared = NewShared();
        using var vm = new TracerouteViewModel(shared);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        shared.IsAutoTraceRunning = true;

        Assert.Contains(nameof(vm.IsAutoTraceRunning), raised);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheSharedState()
    {
        // NetworkSharedState is a DI singleton that outlives the VM, so a leaked handler would keep
        // the VM alive for the whole session and keep raising on a disposed object.
        using var shared = NewShared();
        var vm = new TracerouteViewModel(shared);
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsAutoTraceRunning)) raised++; };

        shared.IsAutoTraceRunning = true;
        Assert.Equal(1, raised);

        vm.Dispose();
        shared.IsAutoTraceRunning = false;

        Assert.Equal(1, raised);   // no further notifications after disposal
    }
}
