// SysManager · NotificationBlockerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for <see cref="NotificationBlockerService"/>. The service takes an
/// injectable registry root; here we point it at a disposable HKCU subkey so the per-app
/// and master toggles can be verified against a real hive without touching the machine's
/// actual notification settings (mirrors <see cref="AppBlockerServiceRegistryTests"/>).
/// </summary>
public sealed class NotificationBlockerServiceTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\NotifBlocker_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly NotificationBlockerService _svc;

    public NotificationBlockerServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _svc = new NotificationBlockerService(_root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    private RegistryKey CreateSender(string aumid, Action<RegistryKey>? seed = null)
    {
        var key = _root.CreateSubKey($@"{NotificationBlockerService.SettingsPath}\{aumid}", writable: true)!;
        seed?.Invoke(key);
        return key;
    }

    // ── Master toggle ──────────────────────────────────────────────────────

    [Fact]
    public void IsGlobalToastEnabled_NoValue_DefaultsTrue()
    {
        Assert.True(_svc.IsGlobalToastEnabled());
    }

    [Fact]
    public void SetGlobalToastEnabled_False_WritesZero_AndReadsBack()
    {
        Assert.True(_svc.SetGlobalToastEnabled(false));
        Assert.False(_svc.IsGlobalToastEnabled());

        using var key = _root.OpenSubKey(NotificationBlockerService.PushKeyPath);
        Assert.Equal(0, key!.GetValue(NotificationBlockerService.ToastValueName));
    }

    [Fact]
    public void SetGlobalToastEnabled_True_DeletesValue_RestoringWindowsDefault()
    {
        _svc.SetGlobalToastEnabled(false);
        Assert.True(_svc.SetGlobalToastEnabled(true));
        Assert.True(_svc.IsGlobalToastEnabled());

        // The exact prior state is "value absent", not "value = 1" — same restore
        // convention as the Gaming Profile NotificationsTweak.
        using var key = _root.OpenSubKey(NotificationBlockerService.PushKeyPath);
        Assert.Null(key!.GetValue(NotificationBlockerService.ToastValueName));
    }

    // ── Per-app senders ────────────────────────────────────────────────────

    [Fact]
    public void GetApps_EmptyRoot_ReturnsEmpty()
    {
        Assert.Empty(_svc.GetApps());
    }

    [Fact]
    public void GetApps_ReadsSender_WithDefaultsWhenValuesAbsent()
    {
        using var _ = CreateSender("com.example.someapp");

        var apps = _svc.GetApps();

        var app = Assert.Single(apps);
        Assert.Equal("com.example.someapp", app.Aumid);
        Assert.True(app.IsEnabled);           // absent Enabled = allowed
        Assert.Equal(0, app.RecentCount);
        Assert.Null(app.LastNotification);
    }

    [Fact]
    public void GetApps_ReadsCountTimestampAndMutedState()
    {
        var stamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Local);
        using var _ = CreateSender("Chrome", k =>
        {
            k.SetValue("Enabled", 0, RegistryValueKind.DWord);
            k.SetValue("PeriodicNotificationCount", 42, RegistryValueKind.DWord);
            k.SetValue("LastNotificationAddedTime", stamp.ToFileTime(), RegistryValueKind.QWord);
        });

        var app = Assert.Single(_svc.GetApps());
        Assert.False(app.IsEnabled);
        Assert.Equal(42, app.RecentCount);
        Assert.Equal(stamp, app.LastNotification);
    }

    [Fact]
    public void GetApps_OrdersByLastNotification_MostRecentFirst()
    {
        var older = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Local);
        var newer = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Local);
        using var _1 = CreateSender("old.app", k => k.SetValue("LastNotificationAddedTime", older.ToFileTime(), RegistryValueKind.QWord));
        using var _2 = CreateSender("new.app", k => k.SetValue("LastNotificationAddedTime", newer.ToFileTime(), RegistryValueKind.QWord));
        using var _3 = CreateSender("never.app");

        var apps = _svc.GetApps();

        Assert.Equal(["new.app", "old.app", "never.app"], apps.Select(a => a.Aumid));
    }

    [Fact]
    public void GetApps_SkipsCorruptTimestamp_LeavesNull()
    {
        using var _ = CreateSender("corrupt.app", k =>
            k.SetValue("LastNotificationAddedTime", long.MaxValue, RegistryValueKind.QWord));

        var app = Assert.Single(_svc.GetApps());
        Assert.Null(app.LastNotification);
    }

    [Fact]
    public void SetAppEnabled_False_WritesZero_AndGetAppsSeesIt()
    {
        using var _ = CreateSender("Slack");

        Assert.True(_svc.SetAppEnabled("Slack", false));

        var app = Assert.Single(_svc.GetApps());
        Assert.False(app.IsEnabled);
    }

    [Fact]
    public void SetAppEnabled_True_DeletesValue_RestoringWindowsDefault()
    {
        using var _ = CreateSender("Slack", k => k.SetValue("Enabled", 0, RegistryValueKind.DWord));

        Assert.True(_svc.SetAppEnabled("Slack", true));

        using var key = _root.OpenSubKey($@"{NotificationBlockerService.SettingsPath}\Slack");
        Assert.Null(key!.GetValue(NotificationBlockerService.EnabledValueName));
        Assert.True(Assert.Single(_svc.GetApps()).IsEnabled);
    }

    [Fact]
    public void SetAppEnabled_UnknownAumid_ReturnsFalse()
    {
        Assert.False(_svc.SetAppEnabled("does.not.exist", false));
    }

    // Negative: the seam's write API must never escape the Settings subtree via a
    // crafted AUMID — separators are rejected before any registry access.
    [Theory]
    [InlineData(@"..\..\Run")]
    [InlineData(@"evil\sub")]
    [InlineData("evil/sub")]
    [InlineData("")]
    [InlineData("   ")]
    public void SetAppEnabled_RejectsPathSeparatorsAndBlank(string aumid)
    {
        Assert.False(_svc.SetAppEnabled(aumid, false));
    }

    // ── Display-name resolution ────────────────────────────────────────────

    [Fact]
    public void ResolveDisplayName_UsesAumidRegistration_WhenPresent()
    {
        using (var reg = _root.CreateSubKey(@"Software\Classes\AppUserModelId\com.example.app", writable: true))
            reg!.SetValue("DisplayName", "Example App");

        Assert.Equal("Example App", _svc.ResolveDisplayName("com.example.app"));
    }

    [Theory]
    [InlineData("com.squirrel.slack.slack", "Slack")]
    [InlineData("Windows.SystemToast.StartupApp", "StartupApp")]
    [InlineData("Microsoft.Office.OUTLOOK.EXE.15", "OUTLOOK")]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe!App", "WindowsStore")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("Zoom", "Zoom")]
    public void PrettifyAumid_ProducesReadableNames(string aumid, string expected)
    {
        Assert.Equal(expected, NotificationBlockerService.PrettifyAumid(aumid));
    }
}
