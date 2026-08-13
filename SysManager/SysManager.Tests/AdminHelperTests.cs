// SysManager · AdminHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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

    /// <summary>
    /// The hint must reach the elevated child through <c>ArgumentList</c>, never concatenated into the
    /// <c>Arguments</c> string. Concatenation lets a hint containing a space split into several
    /// arguments — silently breaking the "return to the right tab" contract, and appending switches to
    /// a process that is about to run with administrator rights.
    /// <para>Asserted against the source, because observing the real command line would mean starting
    /// an elevated process and therefore a UAC prompt. No caller passes a hint today, so this guards
    /// the first one that does.</para>
    /// </summary>
    [Fact]
    public void RelaunchAsAdmin_PassesTheHintAsAnArgument_NotAsConcatenatedText()
    {
        var source = File.ReadAllText(HelperSourcePath());
        var start = source.IndexOf("public static bool RelaunchAsAdmin", StringComparison.Ordinal);
        Assert.True(start >= 0, "RelaunchAsAdmin not found — update this guard.");
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("ArgumentList.Add", body, StringComparison.Ordinal);

        // The specific defect: the hint interpolated beside the sentinel in a single string, and the
        // Arguments property being set at all.
        Assert.DoesNotContain("{RelaunchedElevatedArg} {", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments =", body, StringComparison.Ordinal);

        // The sentinel must still be passed, or the elevated child is treated as a duplicate and the
        // user is left looking at the non-elevated window.
        Assert.Contains("ArgumentList.Add(RelaunchedElevatedArg)", body, StringComparison.Ordinal);
    }

    private static string HelperSourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "SysManager", "Helpers", "AdminHelper.cs");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate AdminHelper.cs from the test output.");
    }
}
