// SysManager · TracerouteMonitorServiceLifecycleTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Lifecycle/regression tests for <see cref="TracerouteMonitorService"/>. The Stop()
/// path was rewritten to mirror <see cref="PingMonitorService"/>: the
/// CancellationTokenSource is disposed only once the loop has finished (deferred via a
/// continuation when the bounded Wait times out), instead of being disposed unconditionally
/// while the still-running pump may use the token — which previously risked an
/// ObjectDisposedException on the background thread. These verify start/stop/dispose is safe
/// and idempotent.
/// </summary>
public class TracerouteMonitorServiceLifecycleTests
{
    [Fact]
    public void StartStop_DoesNotThrow()
    {
        using var svc = new TracerouteMonitorService();
        svc.Start();
        svc.Stop();
    }

    [Fact]
    public void Stop_WithoutStart_IsNoOp()
    {
        using var svc = new TracerouteMonitorService();
        svc.Stop(); // never started — must not throw
    }

    [Fact]
    public void RepeatedStartStop_DoesNotThrow()
    {
        using var svc = new TracerouteMonitorService();
        for (var i = 0; i < 5; i++)
        {
            svc.Start();
            svc.Stop();
        }
    }

    [Fact]
    public void Dispose_AfterStart_DoesNotThrow()
    {
        var svc = new TracerouteMonitorService();
        svc.Start();
        svc.Dispose(); // Dispose() routes through Stop()
    }

    [Fact]
    public void DoubleStart_IsIdempotent()
    {
        using var svc = new TracerouteMonitorService();
        svc.Start();
        svc.Start(); // second Start() must be ignored while running, not leak a CTS
        svc.Stop();
    }

    // The pump runs on the token it was started with, as PingMonitorServiceLifecycleTests pins for the ping
    // monitor, which had the same late read of the field (#2751). The pump is held back and run once the Stop, or
    // the next Start, has happened.

    private static TracerouteMonitorService HeldBack(List<Func<Task>> queued) =>
        new((pump, _) => { queued.Add(pump); return Task.CompletedTask; });

    [Fact]
    public void APumpThatRunsAfterItsStop_EndsAtOnce()
    {
        var queued = new List<Func<Task>>();
        using var svc = HeldBack(queued);
        svc.Start();
        svc.Stop();

        var pump = Assert.Single(queued)();

        Assert.True(pump.IsCompletedSuccessfully);
    }

    [Fact]
    public void APumpThatRunsAfterTheNextStart_DoesNotRunOnItsToken()
    {
        var queued = new List<Func<Task>>();
        using var svc = HeldBack(queued);
        svc.Start();
        svc.Stop();
        svc.Start();

        var first = queued[0]();

        // On the next Start's token it would be a second pump, waiting out the interval before its next round.
        Assert.True(first.IsCompletedSuccessfully);
        svc.Stop();
    }
}
