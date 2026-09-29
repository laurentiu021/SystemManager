// SysManager · WindowsUpdatePolicyServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
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
        Assert.NotNull(p);
        Assert.False(p.DeferFeatureUpdates);
        Assert.False(p.PauseActive);
        Assert.Contains("Default", p.Summary);
    }

    [Fact]
    public void DeferFeatureUpdates_WritesAndReadsBack()
    {
        Assert.True(_svc.DeferFeatureUpdates(45));
        var p = _svc.Read(Now);
        Assert.NotNull(p);
        Assert.True(p.DeferFeatureUpdates);
        Assert.Equal(45, p.FeatureDeferDays);
        Assert.Contains("deferred 45", p.Summary);
    }

    [Fact]
    public void PauseUpdates_SetsBoundedEndTimeInFuture()
    {
        Assert.True(_svc.PauseUpdates(7, Now));
        var p = _svc.Read(Now);
        Assert.NotNull(p);
        Assert.True(p.PauseActive);
        Assert.Equal(Now.AddDays(7), p.PauseUntil);
        Assert.Contains("paused until", p.Summary);
    }

    [Fact]
    public void PauseUpdates_IsClampedToMax()
    {
        Assert.True(_svc.PauseUpdates(999, Now));
        var p = _svc.Read(Now);
        Assert.NotNull(p);
        Assert.Equal(Now.AddDays(WindowsUpdatePolicyService.MaxPauseDays), p.PauseUntil);
    }

    [Fact]
    public void Read_ExpiredPause_IsNotActive()
    {
        _svc.PauseUpdates(7, Now);
        // 10 days later the pause has lapsed.
        var p = _svc.Read(Now.AddDays(10));
        Assert.NotNull(p);
        Assert.False(p.PauseActive);
    }

    [Fact]
    public void RestoreDefault_ClearsAllPolicy()
    {
        _svc.DeferFeatureUpdates(30);
        _svc.PauseUpdates(14, Now);
        Assert.True(_svc.RestoreDefault());

        var p = _svc.Read(Now);
        Assert.NotNull(p);
        Assert.False(p.DeferFeatureUpdates);
        Assert.False(p.PauseActive);
        Assert.Equal(0, p.FeatureDeferDays);
    }

    // ── A policy that could not be read (#2504) ──
    //
    // A refused read returned the defaults, so the tab said "Default — Windows manages update timing." about a PC
    // whose deferral or pause it could not see, and Restore default quoted that as the state it would clear. The key
    // is made unreadable for real, with a deny entry for this user that is removed again afterwards.

    [Fact]
    public void Read_APolicyThatCannotBeRead_IsNull_NotTheDefaults()
    {
        Assert.True(_svc.DeferFeatureUpdates(30));   // the premise: there is a deferral to hide

        using var policy = _root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
            RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(identity.User!,
            RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        var security = policy.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(deny);
        policy.SetAccessControl(security);
        try
        {
            Assert.Null(_svc.Read(Now));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            policy.SetAccessControl(security);
        }

        Assert.True(_svc.Read(Now)?.DeferFeatureUpdates);   // readable again, so the cleanup can delete the key
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
