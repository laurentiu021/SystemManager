using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AdminHelper"/>. These are safe to run on CI
/// (non-admin) and on dev boxes (admin or not).
/// </summary>
public class AdminHelperTests
{
    [Fact]
    public void IsElevated_ReturnsBoolean()
    {
        var result = AdminHelper.IsElevated();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsElevated_IsConsistentAcrossCalls()
    {
        var a = AdminHelper.IsElevated();
        var b = AdminHelper.IsElevated();
        Assert.Equal(a, b);
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

    [Fact]
    public void RelaunchAsAdmin_ReturnsBoolean()
    {
        var result = AdminHelper.RelaunchAsAdmin();
        Assert.IsType<bool>(result);
    }
}
