// SysManager · TracerouteViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.NetworkInformation;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the view model's trace takes the process-wide Network lock, and these tests run its commands. See
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection for why any command counts.
[Collection("ProcessWideStatics")]
public class TracerouteViewModelTests
{
    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultTraceHost_Is8888()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.Equal("8.8.8.8", vm.TraceHost);
    }

    [Fact]
    public void IsTracing_DefaultFalse()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void IsAutoTraceRunning_DefaultFalse()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var vm = new TracerouteViewModel(shared);
        Assert.False(vm.IsAutoTraceRunning);
    }

    [Fact]
    public void CancelTraceCommand_DoesNotThrow()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
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
        new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()),
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
        var raised = vm.RecordPropertyChanges();

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

    [Fact]
    public void StoppingAutoTraceOnThisTab_TakesItsHostOffTheMonitor()
    {
        // The host was never taken out, so it was traced every round until SysManager closed (#2870).
        using var shared = NewShared();
        using var vm = new TracerouteViewModel(shared);
        shared.TrackAutoTraceHost("a.example");
        shared.IsAutoTraceRunning = true;

        vm.StopAutoTraceCommand.Execute(null);

        Assert.False(shared.TraceMonitor.Targets.ContainsKey("a.example"));
    }

    // ---------- Escape (#2597) ----------

    [Fact]
    public void Escape_CancelsTheTrace_OnlyWhileItRuns()
    {
        var shared = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(new Services.PowerShellRunner()), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        using var vm = new TracerouteViewModel(shared);
        Assert.Null(vm.EscapeCancel);

        vm.IsTracing = true;

        Assert.Same(vm.CancelTraceCommand, vm.EscapeCancel);
    }

    // ---------- a finished trace's status and where its route is kept (#2751) ----------
    // The tracer's probe is the test's, three hops to 192.0.2.10, so nothing is sent.

    private static TracerouteService ThreeHops(Func<Task>? beforeEachProbe = null)
    {
        return new TracerouteService(async (destination, ttl, _, ct) =>
        {
            if (beforeEachProbe is not null) await beforeEachProbe().WaitAsync(ct).ConfigureAwait(false);
            // On the pool, as a real probe's answer arrives: a hop is then reported from a thread that is not the UI's.
            await Task.Run(() => { }, ct).ConfigureAwait(false);
            return ttl < 3
                ? new TracerouteService.ProbeReply(IPStatus.TtlExpired, IPAddress.Parse($"198.51.100.{ttl}"))
                : new TracerouteService.ProbeReply(IPStatus.Success, destination);
        });
    }

    private static NetworkSharedState SharedWith(TracerouteService tracer, Action<Action>? postToUi = null) => new(
        new PingMonitorService(), tracer, new TracerouteMonitorService(), new SpeedTestService(new PowerShellRunner()),
        new NetworkRepairService(new PowerShellRunner()), () => null, postToUi);

    [Fact]
    public void AFinishedTrace_SaysDone_AfterAnyHopsUpdateThatWasStillQueued()
    {
        // A hop's update reaches the UI at background priority, behind the UI's own work, and the code after the trace
        // runs ahead of it. The last hop's "Tracing … hop 3" then ran after "Done" and stayed on screen.
        var ui = new BusyUiThread();
        using var shared = SharedWith(ThreeHops(), ui.PostAtBackgroundPriority);
        using var vm = new TracerouteViewModel(shared) { TraceHost = "192.0.2.10" };

        ui.Run(() => vm.TraceCommand.ExecuteAsync(null));

        Assert.Equal("Done — 3 hops", vm.TraceStatus);
    }

    [Fact]
    public async Task ATrace_IsKeptUnderTheHostItTraced_NotWhatTheBoxSaysWhenItEnds()
    {
        var typed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shared = SharedWith(ThreeHops(() => typed.Task));
        using var vm = new TracerouteViewModel(shared) { TraceHost = "192.0.2.10" };

        var tracing = vm.TraceCommand.ExecuteAsync(null);
        vm.TraceHost = "example.org";   // typed while the trace runs
        typed.SetResult();
        await tracing.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["192.0.2.10"], shared.LatestRoutes.Keys);
        Assert.Equal("Done — 3 hops", vm.TraceStatus);
    }

    [Theory]
    [InlineData("198.51.100.2", "Done — 2 hops. 198.51.100.2 answered that it has no route to the destination's network.")]
    [InlineData("*", "Done — 2 hops. A router answered that it has no route to the destination's network.")]
    public void ATraceARouterEnded_SaysWhichRouter_AndWhatItAnswered(string address, string expected)
    {
        IReadOnlyList<TracerouteHop> hops =
        [
            new() { HopNumber = 1, Address = "198.51.100.1", LatencyMs = 4, Status = "TtlExpired" },
            new() { HopNumber = 2, Address = address, LatencyMs = 9, Status = "DestinationNetworkUnreachable" },
        ];

        Assert.Equal(expected, TracerouteViewModel.Finished(hops));
    }

    [Fact]
    public void ATraceThatReachedItsHost_SaysDone_WithNothingAfterIt()
    {
        IReadOnlyList<TracerouteHop> hops =
            [new() { HopNumber = 1, Address = "192.0.2.10", LatencyMs = 4, Status = "Success" }];

        Assert.Equal("Done — 1 hops", TracerouteViewModel.Finished(hops));
    }

    /// <summary>
    /// The UI thread as WPF runs it while it is busy: what an await hands back to it runs first, and what was queued at
    /// background priority waits until that work is done, then runs in the order it was queued.
    /// </summary>
    private sealed class BusyUiThread : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _normal = new();
        private readonly Queue<Action> _background = new();
        private readonly SemaphoreSlim _normalQueued = new(0);
        private int _thread;

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate) _normal.Enqueue(() => d(state));
            _normalQueued.Release();
        }

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>InvokeOnUi's way in: at once on the UI thread, and behind everything else from any other.</summary>
        public void PostAtBackgroundPriority(Action action)
        {
            if (Environment.CurrentManagedThreadId == _thread)
            {
                action();
                return;
            }
            lock (_gate) _background.Enqueue(action);
        }

        /// <summary>
        /// Starts <paramref name="start"/> on this thread and runs the queues until it has ended and both are empty.
        /// </summary>
        public void Run(Func<Task> start)
        {
            _thread = Environment.CurrentManagedThreadId;
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var running = start();
                while (!running.IsCompleted)
                {
                    Assert.True(_normalQueued.Wait(TimeSpan.FromSeconds(30)), "the trace never handed anything back");
                    Action next;
                    lock (_gate) next = _normal.Dequeue();
                    next();
                }

                while (true)
                {
                    Action next;
                    lock (_gate)
                    {
                        if (_normal.Count > 0) next = _normal.Dequeue();
                        else if (_background.Count > 0) next = _background.Dequeue();
                        else break;
                    }
                    next();
                }
                running.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
