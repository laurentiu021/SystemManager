// SysManager · PrivacyMonitorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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

        var entries = _svc.Read();   // must not throw
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
        => Assert.Empty(_svc.Read());

    [Fact]
    public void Read_PackagedApp_WithStartAndStop_NotInUse()
    {
        var start = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var stop = new DateTime(2024, 5, 1, 9, 5, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", start, stop);

        var entries = _svc.Read();
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

        var e = Assert.Single(_svc.Read());
        Assert.Equal("Microphone", e.Capability);
        Assert.True(e.InUse);
        Assert.Equal("In use now", e.LastUsedDisplay);
    }

    [Fact]
    public void Read_NonPackagedApp_IsIncluded()
    {
        var start = new DateTime(2024, 5, 2, 3, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("webcam", "C:#Program Files#Zoom#zoom.exe", start, start + 100, nonPackaged: true);

        var entries = _svc.Read();
        Assert.Contains(entries, e => e.AppName == "zoom.exe" && e.Capability == "Camera");
    }

    [Fact]
    public void Read_InUseEntries_SortFirst()
    {
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        WriteApp("location", "OldApp_x", old, old + 50);                 // finished long ago
        WriteApp("microphone", "LiveApp_y", old + 999_999_999, null);    // in use now

        var entries = _svc.Read();
        Assert.True(entries.Count >= 2);
        Assert.True(entries[0].InUse);   // in-use sorts to the top
    }
}
