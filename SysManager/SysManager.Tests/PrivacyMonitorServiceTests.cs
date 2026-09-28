// SysManager · PrivacyMonitorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyMonitorService"/>. The pure helpers (friendly-name decoding,
/// FILETIME conversion) are tested directly, and the registry reader runs against a
/// redirected HKCU subkey holding a synthetic ConsentStore so no real consent history is
/// needed and nothing on the machine is touched.
/// </summary>
public sealed class PrivacyMonitorServiceTests : IDisposable
{
    private const string ConsentBase =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private readonly string _rootName = @"Software\SysManagerTests\PrivMon_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly PrivacyMonitorService _svc;

    public PrivacyMonitorServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _svc = new PrivacyMonitorService(_root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort */ }
    }

    private void WriteApp(string capability, string appKey, long? start, long? stop, bool nonPackaged = false)
    {
        var path = nonPackaged
            ? $@"{ConsentBase}\{capability}\NonPackaged\{appKey}"
            : $@"{ConsentBase}\{capability}\{appKey}";
        using var k = _root.CreateSubKey(path, writable: true)!;
        if (start.HasValue) k.SetValue("LastUsedTimeStart", start.Value, RegistryValueKind.QWord);
        if (stop.HasValue) k.SetValue("LastUsedTimeStop", stop.Value, RegistryValueKind.QWord);
    }

    // ---------- pure helpers ----------

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Microsoft.WindowsCamera")]
    [InlineData("C:#Program Files#Zoom#zoom.exe", "zoom.exe")]
    [InlineData("SomeApp", "SomeApp")]
    [InlineData("#", "#")]                 // degenerate all-separator key must not throw
    [InlineData("##", "##")]
    [InlineData("###", "###")]
    public void FriendlyAppName_DecodesKeyNames(string key, string expected)
        => Assert.Equal(expected, PrivacyMonitorService.FriendlyAppName(key));

    [Fact]
    public void Read_DegenerateSeparatorKey_DoesNotThrow_AndIsSkippedOrNamed()
    {
        // A consent subkey named only with separators previously crashed FriendlyAppName
        // (Split('#', RemoveEmptyEntries)[^1] on an empty array → IndexOutOfRangeException),
        // which propagated through the eagerly-constructed VM and broke startup.
        var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "##", start, null, nonPackaged: true);
        WriteApp("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", start, start + 10);

        var entries = _svc.Read().Entries;   // must not throw
        // The valid app still surfaces — a degenerate sibling does not abort the scan.
        Assert.Contains(entries, e => e.AppName == "Microsoft.WindowsCamera");
    }

    [Fact]
    public void ToFileTime_RejectsZeroAndNonPositive()
    {
        Assert.Null(PrivacyMonitorService.ToFileTime(0L));
        Assert.Null(PrivacyMonitorService.ToFileTime(null));
        Assert.Equal(123L, PrivacyMonitorService.ToFileTime(123L));
    }

    [Fact]
    public void FileTimeToLocal_ConvertsKnownValue()
    {
        // 2024-01-15T12:00:00Z as a FILETIME.
        var ft = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var local = PrivacyMonitorService.FileTimeToLocal(ft);
        Assert.Equal(new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc), local.ToUniversalTime());
    }

    // ---------- registry reader ----------

    [Fact]
    public void Read_NoConsentStore_ReturnsEmpty()
        => Assert.Empty(_svc.Read().Entries);

    [Fact]
    public async Task ReadAsync_ReturnsSameEntriesAsRead()
    {
        // ReadAsync just runs Read off the UI thread (the VM is built eagerly at startup,
        // so the registry walk must never block or crash the constructor). Same result.
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", start, start + 10);

        var sync = _svc.Read().Entries;
        var async = (await _svc.ReadAsync()).Entries;

        Assert.Equal(sync.Count, async.Count);
        Assert.Contains(async, e => e.AppName == "Microsoft.WindowsCamera");
    }

    [Fact]
    public void Read_PackagedApp_WithStartAndStop_NotInUse()
    {
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var stop = new DateTime(2024, 5, 1, 9, 5, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", start, stop);

        var entries = _svc.Read().Entries;
        var e = Assert.Single(entries);
        Assert.Equal("Camera", e.Capability);
        Assert.Equal("Microsoft.WindowsCamera", e.AppName);
        Assert.False(e.InUse);
        Assert.NotNull(e.LastUsed);
    }

    [Fact]
    public void Read_StartWithoutStop_IsInUse()
    {
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("microphone", "SomeChatApp_abc", start, null);

        var e = Assert.Single(_svc.Read().Entries);
        Assert.Equal("Microphone", e.Capability);
        Assert.True(e.InUse);
        Assert.Equal("In use now", e.LastUsedDisplay);
    }

    [Fact]
    public void Read_NonPackagedApp_IsIncluded()
    {
        var start = new DateTime(2024, 5, 2, 3, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "C:#Program Files#Zoom#zoom.exe", start, start + 100, nonPackaged: true);

        var entries = _svc.Read().Entries;
        Assert.Contains(entries, e => e.AppName == "zoom.exe" && e.Capability == "Camera");
    }

    [Fact]
    public void Read_InUseEntries_SortFirst()
    {
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("location", "OldApp_x", old, old + 50);                 // finished long ago
        WriteApp("microphone", "LiveApp_y", old + 999_999_999, null);    // in use now

        var entries = _svc.Read().Entries;
        Assert.True(entries.Count >= 2);
        Assert.True(entries[0].InUse);   // in-use sorts to the top
    }

    // ---------- a capability that could not be read (#2503) ----------
    //
    // An unreadable capability key was skipped without a trace. With all three unreadable the tab said nothing had
    // been recorded, and with only the camera unreadable it listed the others as if the camera had been checked.
    // The key is made unreadable for real, with a deny entry for this user that is removed again afterwards.

    [Fact]
    public void ACapabilityThatCannotBeRead_IsNamed_AndTheOthersAreStillRead()
    {
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", start, start + 10);
        WriteApp("microphone", "SomeChatApp_abc", start, start + 10);

        using var webcam = _root.OpenSubKey($@"{ConsentBase}\webcam", RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(identity.User!,
            RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        var security = webcam.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(deny);
        webcam.SetAccessControl(security);
        try
        {
            var report = _svc.Read();

            Assert.Equal(["Camera"], report.Unreadable);
            Assert.Contains(report.Entries, e => e.Capability == "Microphone");
            Assert.DoesNotContain(report.Entries, e => e.Capability == "Camera");
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            webcam.SetAccessControl(security);
        }

        Assert.Empty(_svc.Read().Unreadable);   // readable again, so the cleanup can delete the key
    }

    [Fact]
    public void ACapabilityWithNoKey_IsNotUnreadable()
    {
        // Windows creates the key the first time an app asks for that device, so no key is no history.
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("microphone", "SomeChatApp_abc", start, start + 10);

        var report = _svc.Read();

        Assert.Empty(report.Unreadable);
        Assert.Single(report.Entries);
    }
}
