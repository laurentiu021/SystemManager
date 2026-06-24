// SysManager · AdminHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AdminHelper"/>. These are safe to run on CI
/// (non-admin) and on dev boxes (admin or not).
/// </summary>
public class AdminHelperTests
{
    [Fact]
    public void IsElevated_IsConsistentAcrossCalls()
    {
        // Elevation state cannot change during the test process's lifetime, so two
        // calls must agree. (The former IsElevated_ReturnsBoolean test asserted
        // Assert.IsType<bool> on a bool-returning method — always true, tested nothing —
        // and was folded into this real invariant.)
        var a = AdminHelper.IsElevated();
        var b = AdminHelper.IsElevated();
        Assert.Equal(a, b);
    }

    [Fact]
    public void LogElevationDiagnostics_DoesNotThrow()
    {
        // The diagnostic opens the process token via P/Invoke and writes one log line;
        // it must never throw regardless of elevation state or whether a logger sink is
        // configured (it catches the security/access/win32 cases internally).
        var ex = Record.Exception(() => AdminHelper.LogElevationDiagnostics("test"));
        Assert.Null(ex);
    }

    [Fact]
    public void RelaunchedElevatedArg_IsAStableNonEmptySwitch()
    {
        // App.OnStartup matches this exact token in the elevated child's command line to
        // decide whether to wait for the single-instance mutex handover. It must stay a
        // non-empty, whitespace-free switch so it survives argument splitting intact.
        Assert.False(string.IsNullOrWhiteSpace(AdminHelper.RelaunchedElevatedArg));
        Assert.DoesNotContain(' ', AdminHelper.RelaunchedElevatedArg);
        Assert.StartsWith("--", AdminHelper.RelaunchedElevatedArg);
    }

    [Fact]
    public void RelaunchAsAdmin_DoesNotThrow()
    {
        // On CI / non-interactive hosts this will fail to launch (no UAC)
        // but must not throw — it returns false instead.
        // On dev boxes it may actually launch a UAC prompt, but the test
        // process won't wait for it.
        var ex = Record.Exception(() => AdminHelper.RelaunchAsAdmin());
        Assert.Null(ex);
    }

    [Fact]
    public void RelaunchAsAdmin_WithArgumentHint_DoesNotThrow()
    {
        var ex = Record.Exception(() => AdminHelper.RelaunchAsAdmin("--tab=network"));
        Assert.Null(ex);
    }

    // Removed RelaunchAsAdmin_ReturnsBoolean: it asserted Assert.IsType<bool> on a
    // bool-returning method (always true, tested nothing) while needlessly invoking the
    // side-effecting relaunch a third time. RelaunchAsAdmin_DoesNotThrow already covers
    // the call.
}
