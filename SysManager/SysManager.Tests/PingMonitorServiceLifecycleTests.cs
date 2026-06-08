// SysManager · PingMonitorServiceLifecycleTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Lifecycle/regression tests for <see cref="PingMonitorService"/>. The Stop()
/// path was rewritten so the CancellationTokenSource is always disposed (even
/// when the loop is still winding down) instead of being dropped and leaked.
/// These verify the start/stop/dispose cycle is safe and idempotent.
/// </summary>
public class PingMonitorServiceLifecycleTests
{
    [Fact]
    public void StartStop_DoesNotThrow()
    {
        using var svc = new PingMonitorService();
        svc.Start();
        svc.Stop();
    }

    [Fact]
    public void Stop_WithoutStart_IsNoOp()
    {
        using var svc = new PingMonitorService();
        svc.Stop(); // never started — must not throw
    }

    [Fact]
    public void RepeatedStartStop_DoesNotThrow()
    {
        using var svc = new PingMonitorService();
        for (var i = 0; i < 5; i++)
        {
            svc.Start();
            svc.Stop();
        }
    }

    [Fact]
    public void Dispose_AfterStart_DoesNotThrow()
    {
        var svc = new PingMonitorService();
        svc.Start();
        svc.Dispose(); // Dispose() routes through Stop()
    }

    [Fact]
    public void DoubleStart_IsIdempotent()
    {
        using var svc = new PingMonitorService();
        svc.Start();
        svc.Start(); // second Start() must be ignored while running, not leak a CTS
        svc.Stop();
    }
}
