// SysManager · NotificationBlockerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for <see cref="NotificationBlockerService"/>. The service takes an
/// injectable registry root; here we point it at a disposable HKCU subkey so the per-app
/// and master toggles can be verified against a real hive without touching the machine's
/// actual notification settings (mirrors <see cref="AppBlockerServiceRegistryTests"/>).
/// <para>The config directory is injected for the same reason: every <c>SetGlobalToastEnabled</c>
/// increments the master-write ledger, so without it these tests would read and write the
/// developer's real <c>notification-master-writes.json</c> in %LOCALAPPDATA% — the file Gaming
/// Profile consults to decide whether to restore its snapshot.</para>
/// </summary>
public sealed class NotificationBlockerServiceTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\NotifBlocker_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string _configDir;
    private readonly NotificationBlockerService _svc;

    public NotificationBlockerServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _configDir = Path.Combine(Path.GetTempPath(), "SysManagerNotifTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _svc = new NotificationBlockerService(_root, _configDir);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
        try { if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
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

    // ── The master-write ledger's read-increment-write ─────────────────────

    [Fact]
    public void SetGlobalToastEnabled_IncrementsTheMasterWriteLedger()
    {
        // The single-threaded baseline the race below builds on: without this, a race test that
        // asserted "2" could pass simply because nothing ever counted.
        Assert.Equal(0, NotificationBlockerService.ReadMasterToggleWriteCount(_configDir));

        _svc.SetGlobalToastEnabled(false);
        Assert.Equal(1, NotificationBlockerService.ReadMasterToggleWriteCount(_configDir));

        _svc.SetGlobalToastEnabled(true);
        Assert.Equal(2, NotificationBlockerService.ReadMasterToggleWriteCount(_configDir));
    }

    /// <summary>
    /// How many times the race below is run. The unsynchronized code only loses an increment on the
    /// interleavings where both callers read the counter before either writes it, so a single attempt
    /// could step straight over the bug. Once the read-increment-write is serialized, EVERY attempt
    /// reaches 2 by construction, so the repetition cannot make this flaky.
    /// </summary>
    private const int RaceAttempts = 64;

    /// <summary>Generous enough never to trip on a loaded CI runner; short enough to fail rather than hang.</summary>
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwoMasterToggleWritesAtOnce_BothIncrementsAreCounted()
    {
        // RecordMasterToggleWrite reads the counter, adds one, and writes it back. AtomicFile makes the
        // write atomic but not the pair: unsynchronized, both callers read N and both write N+1, so one
        // increment vanishes. What that costs is the discriminator this ledger exists to be — Gaming
        // Profile compares the count it recorded at apply against the count at revert, and a count that
        // looks unchanged makes it restore its snapshot over a toggle the user moved themselves.
        for (var attempt = 0; attempt < RaceAttempts; attempt++)
        {
            var dir = Path.Combine(_configDir, $"race-{attempt}");
            Directory.CreateDirectory(dir);
            // One instance, as in the container: the lock is per-instance, so a second service would
            // be a different race and would not prove this one.
            var svc = new NotificationBlockerService(_root, dir);

            using var ready = new CountdownEvent(2);
            using var go = new ManualResetEventSlim(false);
            var writers = new[]
            {
                Task.Run(() => { ready.Signal(); go.Wait(); svc.SetGlobalToastEnabled(false); }),
                Task.Run(() => { ready.Signal(); go.Wait(); svc.SetGlobalToastEnabled(true); }),
            };

            Assert.True(ready.Wait(RaceTimeout), "the racing writers never reached the start line");
            go.Set();
            // WaitAsync throws TimeoutException on the bound and rethrows any writer fault, so
            // neither a hang nor an exception inside a writer is swallowed.
            await Task.WhenAll(writers).WaitAsync(RaceTimeout);

            // Named attempt, no path in the message: a failure here is printed in public CI output,
            // and the temp directories carry the account name.
            var counted = NotificationBlockerService.ReadMasterToggleWriteCount(dir);
            Assert.True(counted == 2,
                $"attempt {attempt}: the ledger counted {counted} of 2 master-toggle writes — both "
                    + "callers read the counter before either wrote it, so an increment was lost");
        }
    }
}
