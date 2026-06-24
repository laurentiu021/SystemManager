// SysManager · WindowsUpdatePolicyServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for <see cref="WindowsUpdatePolicyService"/>. The service takes an
/// injectable registry root; here we point it at a disposable HKCU subkey so defer/pause/
/// restore can be verified against a real hive without administrator rights and without
/// touching the machine's real Windows Update policy.
/// </summary>
public sealed class WindowsUpdatePolicyServiceTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\WUPolicy_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly WindowsUpdatePolicyService _svc;
    private static readonly DateTime Now = new(2026, 6, 24, 12, 0, 0, DateTimeKind.Local);

    public WindowsUpdatePolicyServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _svc = new WindowsUpdatePolicyService(_root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort */ }
    }

    [Fact]
    public void Read_NoPolicy_ReturnsDefaults()
    {
        var p = _svc.Read(Now);
        Assert.False(p.DeferFeatureUpdates);
        Assert.False(p.PauseActive);
        Assert.Contains("Default", p.Summary);
    }

    [Fact]
    public void DeferFeatureUpdates_WritesAndReadsBack()
    {
        Assert.True(_svc.DeferFeatureUpdates(45));
        var p = _svc.Read(Now);
        Assert.True(p.DeferFeatureUpdates);
        Assert.Equal(45, p.FeatureDeferDays);
        Assert.Contains("deferred 45", p.Summary);
    }

    [Fact]
    public void PauseUpdates_SetsBoundedEndTimeInFuture()
    {
        Assert.True(_svc.PauseUpdates(7, Now));
        var p = _svc.Read(Now);
        Assert.True(p.PauseActive);
        Assert.Equal(Now.AddDays(7), p.PauseUntil);
        Assert.Contains("paused until", p.Summary);
    }

    [Fact]
    public void PauseUpdates_IsClampedToMax()
    {
        Assert.True(_svc.PauseUpdates(999, Now));
        var p = _svc.Read(Now);
        Assert.Equal(Now.AddDays(WindowsUpdatePolicyService.MaxPauseDays), p.PauseUntil);
    }

    [Fact]
    public void Read_ExpiredPause_IsNotActive()
    {
        _svc.PauseUpdates(7, Now);
        // 10 days later the pause has lapsed.
        var p = _svc.Read(Now.AddDays(10));
        Assert.False(p.PauseActive);
    }

    [Fact]
    public void RestoreDefault_ClearsAllPolicy()
    {
        _svc.DeferFeatureUpdates(30);
        _svc.PauseUpdates(14, Now);
        Assert.True(_svc.RestoreDefault());

        var p = _svc.Read(Now);
        Assert.False(p.DeferFeatureUpdates);
        Assert.False(p.PauseActive);
        Assert.Equal(0, p.FeatureDeferDays);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(30, 30)]
    [InlineData(9999, 365)]
    public void ClampDeferDays_BoundsTo0To365(int input, int expected)
        => Assert.Equal(expected, WindowsUpdatePolicyService.ClampDeferDays(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(999, 35)]
    public void ClampPauseDays_BoundsTo1To35(int input, int expected)
        => Assert.Equal(expected, WindowsUpdatePolicyService.ClampPauseDays(input));
}
