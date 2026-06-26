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
}
