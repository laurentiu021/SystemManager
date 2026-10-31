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
        using var settings = _root.OpenSubKey(WindowsUpdatePolicyService.SettingsPath)!;
        Assert.Empty(settings.GetValueNames());
    }

    // ── The pause Windows applies (#2726) ──
    //
    // The pause went into the policy key as start and end times. The policy has only a start date, and a pause there
    // lasts 35 days from it, so the pause the tab showed was not the one Windows applied.

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    [Fact]
    public void PauseUpdates_WritesTheSettingsAppsPause_InTheFormItWritesIt()
    {
        Assert.True(_svc.PauseUpdates(7, Now));

        var start = Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var end = Now.ToUniversalTime().AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        using var settings = _root.OpenSubKey(WindowsUpdatePolicyService.SettingsPath)!;
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["PauseUpdatesStartTime"] = start,
                ["PauseFeatureUpdatesStartTime"] = start,
                ["PauseQualityUpdatesStartTime"] = start,
                ["PauseUpdatesExpiryTime"] = end,
                ["PauseFeatureUpdatesEndTime"] = end,
                ["PauseQualityUpdatesEndTime"] = end,
            },
            settings.GetValueNames().ToDictionary(name => name, name => settings.GetValue(name) as string));
        Assert.Equal(RegistryValueKind.String, settings.GetValueKind("PauseUpdatesExpiryTime"));

        // Nothing in the policy key, where a start date would pause updates for 35 days whatever was chosen.
        using var policy = _root.OpenSubKey(PolicyKey);
        Assert.True(policy is null || policy.GetValueNames().Length == 0,
            "the pause wrote into the policy key: " + string.Join(", ", policy?.GetValueNames() ?? []));
    }

    [Fact]
    public void PauseUpdates_ClearsThePauseAnEarlierVersionLeftInThePolicy_AndEndsWhenChosen()
    {
        using (var policy = _root.CreateSubKey(PolicyKey, writable: true)!)
        {
            policy.SetValue("DeferFeatureUpdates", 1, RegistryValueKind.DWord);
            foreach (var name in WindowsUpdatePolicyService.PolicyPauseValues)
                policy.SetValue(name, Now.AddDays(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.True(_svc.PauseUpdates(3, Now));

        using (var policy = _root.OpenSubKey(PolicyKey)!)
            Assert.Equal(["DeferFeatureUpdates"], policy.GetValueNames());
        Assert.Equal(Now.AddDays(3), _svc.Read(Now)?.PauseUntil);
    }

    [Fact]
    public void Read_AStartDateInThePolicy_PausesForThirtyFiveDaysFromIt()
    {
        // How Group Policy writes it: a yyyy-mm-dd date, and the pause lasts 35 days from it.
        using (var policy = _root.CreateSubKey(PolicyKey, writable: true)!)
            policy.SetValue("PauseQualityUpdatesStartTime", "2026-06-20");

        Assert.Equal(new DateTime(2026, 7, 25), _svc.Read(Now)?.PauseUntil);
        Assert.False(_svc.Read(new DateTime(2026, 7, 25, 0, 0, 1, DateTimeKind.Local))?.PauseActive);
    }

    [Fact]
    public void Read_TheStartTimeAnEarlierVersionWrote_IsReadByItsDate()
    {
        // A 7-day pause from an earlier version: its start and end times in the policy key. Windows reads no end
        // there, so the tab now says what the start date allows, not the end that is no policy.
        using (var policy = _root.CreateSubKey(PolicyKey, writable: true)!)
        {
            policy.SetValue("PauseFeatureUpdatesStartTime", "2026-06-24T12:00:00.0000000+03:00");
            policy.SetValue("PauseFeatureUpdatesEndTime", "2026-07-01T12:00:00.0000000+03:00");
        }

        Assert.Equal(new DateTime(2026, 7, 29), _svc.Read(Now)?.PauseUntil);
    }

    [Theory]
    [InlineData("2026-07-01T09:00:00Z")]
    [InlineData("2026-07-01T12:00:00.0000000+03:00")]
    public void Read_ASettingsPause_IsReadInAnyIsoForm(string expiry)
    {
        // Another tool, or another Windows, may write the same value another way.
        using (var settings = _root.CreateSubKey(WindowsUpdatePolicyService.SettingsPath, writable: true)!)
            settings.SetValue("PauseUpdatesExpiryTime", expiry);

        Assert.Equal(new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc).ToLocalTime(), _svc.Read(Now)?.PauseUntil);
    }

    [Fact]
    public void Read_ASettingsPauseThatIsNoTime_IsNoPause()
    {
        using (var settings = _root.CreateSubKey(WindowsUpdatePolicyService.SettingsPath, writable: true)!)
            settings.SetValue("PauseUpdatesExpiryTime", "not a time");

        var p = _svc.Read(Now);
        Assert.NotNull(p);
        Assert.False(p.PauseActive);
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
