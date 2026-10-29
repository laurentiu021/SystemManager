// SysManager · RegistryPathsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="RegistryPaths.OpenOrCreateKey"/>, the hive-parsing and open/create logic
/// PrivacyService and SettingsWatchdogService shared as two byte-identical private copies before
/// this (#2860).
/// </summary>
public sealed class RegistryPathsTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\RegistryPaths_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public RegistryPathsTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);

    [Theory]
    [InlineData("HKCU")]
    [InlineData("HKEY_CURRENT_USER")]
    [InlineData("hkcu")]
    public void OpenOrCreateKey_RecognisedHkcuSpelling_ResolvesToTheGivenHkcuRoot(string hiveName)
    {
        using var key = RegistryPaths.OpenOrCreateKey(_root, _root, $@"{hiveName}\Sub", writable: true);
        Assert.NotNull(key);
    }

    [Theory]
    [InlineData("HKLM")]
    [InlineData("HKEY_LOCAL_MACHINE")]
    public void OpenOrCreateKey_RecognisedHklmSpelling_ResolvesToTheGivenHklmRoot(string hiveName)
    {
        // Both roots point at the same throwaway key here — the point is that SOME root resolved
        // and the write landed, not which of the two.
        using var key = RegistryPaths.OpenOrCreateKey(_root, _root, $@"{hiveName}\Sub", writable: true);
        Assert.NotNull(key);
    }

    [Fact]
    public void OpenOrCreateKey_UnrecognisedHive_ReturnsNull()
    {
        using var key = RegistryPaths.OpenOrCreateKey(_root, _root, @"HKU\Sub", writable: false);
        Assert.Null(key);
    }

    [Fact]
    public void OpenOrCreateKey_NoSeparator_ReturnsNull()
    {
        using var key = RegistryPaths.OpenOrCreateKey(_root, _root, "HKCU", writable: false);
        Assert.Null(key);
    }

    [Fact]
    public void OpenOrCreateKey_Writable_CreatesTheSubkeyIfMissing()
    {
        using (var key = RegistryPaths.OpenOrCreateKey(_root, _root, @"HKCU\Created\Nested", writable: true))
            Assert.NotNull(key);

        using var reopened = _root.OpenSubKey(@"Created\Nested");
        Assert.NotNull(reopened);
    }

    [Fact]
    public void OpenOrCreateKey_NotWritable_DoesNotCreateAMissingSubkey()
    {
        using var key = RegistryPaths.OpenOrCreateKey(_root, _root, @"HKCU\NeverCreated", writable: false);
        Assert.Null(key);
        Assert.Null(_root.OpenSubKey("NeverCreated"));
    }

    [Fact]
    public void OpenOrCreateKey_WritesAndReadsBackAValue()
    {
        using (var key = RegistryPaths.OpenOrCreateKey(_root, _root, @"HKCU\RoundTrip", writable: true)!)
            key.SetValue("Val", 7, RegistryValueKind.DWord);

        using var readBack = RegistryPaths.OpenOrCreateKey(_root, _root, @"HKCU\RoundTrip", writable: false)!;
        Assert.Equal(7, readBack.GetValue("Val"));
    }

    [Fact]
    public void TheSixSharedPaths_AreAllWellFormedHiveRootedPaths()
    {
        // Pinned by value, not just by not-null: a future edit that quietly changes one of these six
        // changes what PrivacyService writes and what SettingsWatchdogService re-reads, together.
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", RegistryPaths.DataCollectionPolicy);
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", RegistryPaths.WindowsSystemPolicy);
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", RegistryPaths.AdvertisingInfo);
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", RegistryPaths.ContentDeliveryManager);
        Assert.Equal(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", RegistryPaths.ExplorerPolicy);
        Assert.Equal(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", RegistryPaths.Dsh);
    }
}
