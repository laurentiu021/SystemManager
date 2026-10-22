// SysManager · NotificationBlockerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for <see cref="NotificationBlockerService"/>. The service takes an
/// injectable registry root; here we point it at a disposable HKCU subkey so the per-app
/// and main toggles can be verified against a real hive without touching the machine's
/// actual notification settings (mirrors <see cref="AppBlockerServiceRegistryTests"/>).
/// <para>The config directory is injected for the same reason: every <c>SetGlobalToastEnabled</c>
/// increments the toggle-write ledger, so without it these tests would read and write the
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

    // ── Main toggle ────────────────────────────────────────────────────────

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

    // Negative: the seam's write API must never reach a key outside the Settings subtree, or one an ID does not name,
    // through a crafted AUMID. A backslash keeps the parts of an ID that is a program's path (#2838), but every part
    // must be a key name. RegistryKey collapses a doubled backslash and drops a trailing one, so those would reach
    // another sender's key; ".", "..", a forward slash and a blank ID are refused with them.
    [Theory]
    [InlineData(@"Contoso\\App")]
    [InlineData(@"Contoso\App\")]
    [InlineData(@"Contoso\")]
    [InlineData(@"\Contoso\App")]
    [InlineData(@"Contoso\.\App")]
    [InlineData(@"Contoso\App\..")]
    [InlineData(@"..\..\Run")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Contoso/App")]
    [InlineData("")]
    [InlineData("   ")]
    public void SetAppEnabled_RefusesAnIdThatIsNotAPathOfKeyNames(string aumid)
    {
        // The keys a lenient reading of these IDs would reach, so a refusal is not only a key that is not there.
        using var folder = CreateSender("Contoso");
        using var app = CreateSender(@"Contoso\App");

        Assert.False(_svc.SetAppEnabled(aumid, false));

        Assert.Null(folder.GetValue(NotificationBlockerService.EnabledValueName));
        Assert.Null(app.GetValue(NotificationBlockerService.EnabledValueName));
    }

    [Theory]
    [InlineData("Slack", true)]
    [InlineData(PowerShellId, true)]
    [InlineData(@"Contoso\..\App", false)]
    [InlineData(@"Contoso\.\App", false)]
    [InlineData(@"Contoso\\App", false)]
    [InlineData(@"\Contoso", false)]
    [InlineData(@"Contoso\", false)]
    [InlineData("Contoso/App", false)]
    public void IsSenderPath_AcceptsOnlyAPathOfKeyNames(string aumid, bool expected)
    {
        Assert.Equal(expected, NotificationBlockerService.IsSenderPath(aumid));
    }

    // ── Senders whose ID is a program's path (#2838) ───────────────────────

    // PowerShell's ID: the System folder's known-folder ID, then the program's path under it.
    private const string SystemFolder = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}";
    private const string PowerShellId = SystemFolder + @"\WindowsPowerShell\v1.0\powershell.exe";

    // A known folder only these tests know, where the test assembly stands in for a program: it is on every PC that
    // runs them, and it has a description.
    private static readonly Guid TestFolder = new("5D2F7C41-8A3B-4E69-B0D1-2C7E9F4A6B38");
    private static readonly string ThisFile = typeof(NotificationBlockerServiceTests).Assembly.Location;
    private static readonly string ThisFileId = $@"{TestFolder:B}\{Path.GetFileName(ThisFile)}";

    private static Func<Guid, string?> FolderAt(string path) => id => id == TestFolder ? path : null;

    // A service that finds no known folder, so a program's sender is named from its ID on any PC.
    private NotificationBlockerService WithoutFolders() => new(_root, _configDir, _ => null);

    // A sender's values: one recent notification, so its key is a sender's and not a folder's.
    private static void OneRecent(RegistryKey key) =>
        key.SetValue("PeriodicNotificationCount", 1, RegistryValueKind.DWord);

    [Fact]
    public void GetApps_ListsASenderWhoseIdIsAPath_OnceUnderItsWholeId()
    {
        var stamp = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Local);
        using var _ = CreateSender(PowerShellId, k =>
        {
            k.SetValue("PeriodicNotificationCount", 3, RegistryValueKind.DWord);
            k.SetValue("LastNotificationAddedTime", stamp.ToFileTime(), RegistryValueKind.QWord);
        });

        var app = Assert.Single(WithoutFolders().GetApps());

        Assert.Equal(PowerShellId, app.Aumid);
        Assert.Equal("Powershell", app.DisplayName);
        Assert.Equal(3, app.RecentCount);
        Assert.Equal(stamp, app.LastNotification);
    }

    [Fact]
    public void GetApps_ListsEachProgramUnderAFolder_AndNotTheFolder()
    {
        const string cmdId = SystemFolder + @"\cmd.exe";
        using var _1 = CreateSender(PowerShellId, OneRecent);
        using var _2 = CreateSender(cmdId, k => k.SetValue("PeriodicNotificationCount", 2, RegistryValueKind.DWord));
        using var _3 = CreateSender("Slack");

        Assert.Equal([cmdId, PowerShellId, "Slack"], WithoutFolders().GetApps().Select(a => a.Aumid));
    }

    [Fact]
    public void GetApps_DoesNotListASendersOwnSubkeys()
    {
        using var _ = CreateSender("Contoso.App", k =>
        {
            k.SetValue("PeriodicNotificationCount", 1, RegistryValueKind.DWord);
            k.CreateSubKey("Details")!.Dispose();
        });

        Assert.Equal(["Contoso.App"], WithoutFolders().GetApps().Select(a => a.Aumid));
    }

    [Fact]
    public void GetApps_ListsASenderAsDeepAsALongProgramPath()
    {
        // Nine parts, as a program kept in a versioned folder of a per-user install has.
        const string id = @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\Contoso\Toolbox\apps\Studio"
                          + @"\ch-0\233.11799\bin\studio64.exe";
        using var _ = CreateSender(id, OneRecent);

        Assert.Equal([id], WithoutFolders().GetApps().Select(a => a.Aumid));
    }

    [Fact]
    public void GetApps_NamesASenderWhoseIdIsAProgramsPath_ByTheProgramsDescription()
    {
        var expected = FileVersionInfo.GetVersionInfo(ThisFile).FileDescription?.Trim();
        Assert.False(string.IsNullOrEmpty(expected));   // without one, the case below would prove nothing
        using var _ = CreateSender(ThisFileId, OneRecent);

        var svc = new NotificationBlockerService(_root, _configDir, FolderAt(Path.GetDirectoryName(ThisFile)!));

        Assert.Equal(expected, Assert.Single(svc.GetApps()).DisplayName);
    }

    [Fact]
    public void SetAppEnabled_ASenderWhoseIdIsAPath_MutesTheKeyWindowsReads_NotTheFolder()
    {
        var svc = WithoutFolders();
        using var sender = CreateSender(PowerShellId, OneRecent);

        Assert.True(svc.SetAppEnabled(PowerShellId, false));

        Assert.Equal(0, sender.GetValue(NotificationBlockerService.EnabledValueName));
        using (var folder = _root.OpenSubKey($@"{NotificationBlockerService.SettingsPath}\{SystemFolder}"))
            Assert.Null(folder!.GetValue(NotificationBlockerService.EnabledValueName));
        Assert.False(Assert.Single(svc.GetApps()).IsEnabled);

        Assert.True(svc.SetAppEnabled(PowerShellId, true));

        Assert.Null(sender.GetValue(NotificationBlockerService.EnabledValueName));
    }

    [Fact]
    public void SetAppEnabled_APathIdWithNoKey_IsRefused_AndCreatesNothing()
    {
        using var _ = CreateSender("Slack");

        Assert.False(_svc.SetAppEnabled(PowerShellId, false));

        using var settings = _root.OpenSubKey(NotificationBlockerService.SettingsPath);
        Assert.Equal(["Slack"], settings!.GetSubKeyNames());
    }

    [Fact]
    public void ProgramDescription_IsTheDescriptionOfTheFileTheIdNames()
    {
        var expected = FileVersionInfo.GetVersionInfo(ThisFile).FileDescription?.Trim();
        Assert.False(string.IsNullOrEmpty(expected));   // without one, the case below would prove nothing

        var found = NotificationBlockerService.ProgramDescription(
            ThisFileId, FolderAt(Path.GetDirectoryName(ThisFile)!));

        Assert.Equal(expected, found);
    }

    [Fact]
    public void ProgramDescription_IsNull_ForAPathThatLeavesTheFolder()
    {
        // The test assembly's whole path after the folder's ID. Its drive part roots the path outside the folder,
        // every part is a key name, and the file is there, so only the confinement to the folder can refuse it.
        var id = $@"{TestFolder:B}\{ThisFile}";
        Assert.True(NotificationBlockerService.IsSenderPath(id));

        var found = NotificationBlockerService.ProgramDescription(
            id, FolderAt(Path.Combine(Path.GetDirectoryName(ThisFile)!, "Programs")));

        Assert.Null(found);
    }

    // {0} is the test folder's ID, {1} the test assembly's file name, {2} an ID the lookup does not resolve, and {3}
    // the test folder's ID without the braces Windows writes it with.
    [Theory]
    [InlineData("{0}")]
    [InlineData(@"{0}\missing.exe")]
    [InlineData(@"{0}\..\{1}")]
    [InlineData(@"{2}\{1}")]
    [InlineData(@"{3}\{1}")]
    [InlineData(@"Contoso\{1}")]
    public void ProgramDescription_IsNull_ForAnIdThatNamesNoProgramInItsFolder(string shape)
    {
        var id = string.Format(CultureInfo.InvariantCulture, shape,
            TestFolder.ToString("B"), Path.GetFileName(ThisFile), Guid.Empty.ToString("B"), TestFolder.ToString("D"));

        Assert.Null(NotificationBlockerService.ProgramDescription(id, FolderAt(Path.GetDirectoryName(ThisFile)!)));
    }

    [Fact]
    public void ProgramDescription_IsNull_ForAFileWithNoDescription()
    {
        File.WriteAllText(Path.Combine(_configDir, "notes.txt"), "not a program");

        Assert.Null(NotificationBlockerService.ProgramDescription($@"{TestFolder:B}\notes.txt", FolderAt(_configDir)));
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
    [InlineData(PowerShellId, "Powershell")]
    [InlineData(@"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\Programs\my_tool\my_tool.exe", "My_tool")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Contoso\Contoso.Notes.exe", "Contoso.Notes")]
    public void PrettifyAumid_ProducesReadableNames(string aumid, string expected)
    {
        Assert.Equal(expected, NotificationBlockerService.PrettifyAumid(aumid));
    }

    // ── The toggle-write ledger's read-increment-write ─────────────────────

    [Fact]
    public void SetGlobalToastEnabled_IncrementsTheToggleWriteLedger()
    {
        // The single-threaded baseline the race below builds on: without this, a race test that
        // asserted "2" could pass simply because nothing ever counted.
        Assert.Equal(0, NotificationBlockerService.ReadMainToggleWriteCount(_configDir));

        _svc.SetGlobalToastEnabled(false);
        Assert.Equal(1, NotificationBlockerService.ReadMainToggleWriteCount(_configDir));

        _svc.SetGlobalToastEnabled(true);
        Assert.Equal(2, NotificationBlockerService.ReadMainToggleWriteCount(_configDir));
    }

    /// <summary>
    /// How many times the race below is run. The unsynchronized code only loses an increment on the
    /// interleavings where both callers read the counter before either writes it, so a single attempt
    /// could step straight over the bug. Once the read-increment-write is serialized, EVERY attempt
    /// reaches 2 by construction, so the repetition cannot make this flaky.
    /// </summary>
    private const int RaceAttempts = 64;

    [Fact]
    public async Task TwoMainToggleWritesAtOnce_BothIncrementsAreCounted()
    {
        // RecordMainToggleWrite reads the counter, adds one, and writes it back. AtomicFile makes the
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

            await StartLine.RaceAsync(() => svc.SetGlobalToastEnabled(false), () => svc.SetGlobalToastEnabled(true));

            // Named attempt, no path in the message: a failure here is printed in public CI output,
            // and the temp directories carry the account name.
            var counted = NotificationBlockerService.ReadMainToggleWriteCount(dir);
            Assert.True(counted == 2,
                $"attempt {attempt}: the ledger counted {counted} of 2 main-toggle writes — both "
                    + "callers read the counter before either wrote it, so an increment was lost");
        }
    }
}
