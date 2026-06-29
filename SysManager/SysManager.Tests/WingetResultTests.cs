// SysManager · WingetResultTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class WingetResultTests
{
    [Fact]
    public void From_Zero_IsSucceededAndUpdated()
    {
        var r = WingetResult.From(0);
        Assert.True(r.Succeeded);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Updated", r.FriendlyMessage);
    }

    [Fact]
    public void From_NonZero_IsNotSucceeded()
    {
        var r = WingetResult.From(unchecked((int)0x8A150011));
        Assert.False(r.Succeeded);
        Assert.Equal("No applicable update found", r.FriendlyMessage);
    }

    [Theory]
    [InlineData(0x8A150109u, "Update installed — restart required")]
    [InlineData(0x8A150049u, "Another install is in progress — try again shortly")]
    [InlineData(0x8A15010Cu, "App is running — close it and retry")]
    [InlineData(0x8A150010u, "Couldn't find the app in the catalog")]
    public void Describe_KnownCodes_AreFriendly(uint code, string expected)
        => Assert.Equal(expected, WingetExitCodes.Describe(unchecked((int)code)));

    [Fact]
    public void Describe_UnknownCode_FallsBackToHex_NotRawDecimal()
    {
        // An unmapped non-zero code must render as hex, never a bare signed decimal
        // like "exit -1978335189" (the thing issue #1130 complained about).
        var msg = WingetExitCodes.Describe(unchecked((int)0x8A150099));
        Assert.Equal("Failed (winget code 0x8A150099)", msg);
        Assert.DoesNotContain("-", msg);
    }

    [Fact]
    public void Cancelled_IsNotSucceeded()
    {
        Assert.False(WingetResult.Cancelled.Succeeded);
        Assert.Equal("Cancelled", WingetResult.Cancelled.FriendlyMessage);
    }
}
