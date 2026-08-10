// SysManager · NetworkSharedStateAutoTraceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// The auto-trace monitor is a SINGLE instance shared by the Ping tab and the Traceroute tab:
/// <see cref="NetworkSharedState.StartMonitoring"/> / <see cref="NetworkSharedState.StopMonitoring"/>
/// (what Ping's Start/Stop buttons call) drive the same <c>TraceMonitor</c> that
/// <see cref="TracerouteViewModel"/> starts and stops. These live here rather than in the unit suite
/// because they call the real <c>StartMonitoring</c>, which starts <see cref="PingMonitorService"/>
/// and emits ICMP on its first tick. Every seeded target is replaced with a non-routable RFC 5737
/// TEST-NET-1 address first, so nothing reaches the network — matching PingMonitorServiceTests.
/// </summary>
[Collection("Network")]
public class NetworkSharedStateAutoTraceTests
{
    private const string UnreachableHost = "192.0.2.1"; // RFC 5737 TEST-NET-1

    /// <summary>
    /// Shared state whose ping targets cannot leave the machine. The constructor seeds a Gateway
    /// target from the real routing table, so it is disabled before anything is started.
    /// </summary>
    private static NetworkSharedState NewIsolatedShared()
    {
        var shared = new NetworkSharedState(
            new PingMonitorService { Interval = TimeSpan.FromMilliseconds(100), TimeoutMs = 300 },
            new TracerouteService(),
            new TracerouteMonitorService(),
            new SpeedTestService(),
            new NetworkRepairService(new PowerShellRunner()));

        foreach (var target in shared.Targets)
        {
            target.IsEnabled = false;
            target.Host = UnreachableHost;
        }

        return shared;
    }

    [Fact]
    public void PingStop_TurnsOffTheAutoTraceFlag_SoTheTraceTabCannotClaimItIsStillRunning()
    {
        // The reported desync: Ping's Stop calls StopMonitoring, which really does call
        // TraceMonitor.Stop() — yet the Traceroute tab kept showing "Stop auto-trace" and claimed
        // "Auto-trace running" for a monitor that was already dead.
        using var shared = NewIsolatedShared();
        using var vm = new TracerouteViewModel(shared);

        shared.StartMonitoring();                        // == the user pressing Start on Ping
        Assert.True(shared.TraceMonitor.IsRunning);      // the monitor really is running
        Assert.True(vm.IsAutoTraceRunning);              // …so the Traceroute tab must say so

        shared.StopMonitoring();                         // == the user pressing Stop on Ping

        Assert.False(shared.TraceMonitor.IsRunning);
        Assert.False(vm.IsAutoTraceRunning);             // was stuck true before the fix
    }

    [Fact]
    public void PingStart_TurnsOnTheAutoTraceFlag_SoTheTraceTabDoesNotOfferToStartItAgain()
    {
        // The other half: Start on Ping begins auto-traceroutes against every enabled target while
        // the Traceroute tab still displayed "Start auto-trace", as if nothing were running.
        using var shared = NewIsolatedShared();
        using var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsAutoTraceRunning);

        shared.StartMonitoring();

        Assert.True(shared.TraceMonitor.IsRunning);
        Assert.True(vm.IsAutoTraceRunning);              // was stuck false before the fix

        shared.StopMonitoring();
    }

    [Fact]
    public void TheFlagAlwaysAgreesWithTheMonitor_AcrossBothTabsStartingAndStopping()
    {
        // Whichever tab acts, the flag and the monitor must never disagree — that agreement is the
        // whole point of moving the state onto the shared object.
        using var shared = NewIsolatedShared();
        using var vm = new TracerouteViewModel(shared);

        shared.StartMonitoring();
        Assert.Equal(shared.TraceMonitor.IsRunning, vm.IsAutoTraceRunning);

        vm.StopAutoTraceCommand.Execute(null);           // stopped from the OTHER tab
        Assert.Equal(shared.TraceMonitor.IsRunning, vm.IsAutoTraceRunning);
        Assert.False(vm.IsAutoTraceRunning);

        shared.StartMonitoring();                        // restarted from Ping
        Assert.Equal(shared.TraceMonitor.IsRunning, vm.IsAutoTraceRunning);
        Assert.True(vm.IsAutoTraceRunning);

        shared.StopMonitoring();
        Assert.Equal(shared.TraceMonitor.IsRunning, vm.IsAutoTraceRunning);
    }
}
