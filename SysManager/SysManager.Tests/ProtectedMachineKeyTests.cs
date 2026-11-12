// SysManager · ProtectedMachineKeyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="ProtectedMachineKey"/>'s decisions that need no key of HKLM's: which access lists it trusts, when the
/// checks cannot be left out, and which keys it will create at all.
/// </summary>
public sealed class ProtectedMachineKeyTests
{
    private static RegistrySecurity UserWritable()
    {
        var security = ProtectedMachineKey.CreateSecurity();
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            RegistryRights.SetValue,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// A key is trusted only when its own access list and SOFTWARE's are both protected. Every stand-in key a test can
    /// make has a user-writable SOFTWARE, so the key's own list is checked here, over lists built in memory.
    /// </summary>
    [Fact]
    public void IsTrusted_NeedsTheParentAndTheKeyBothProtected()
    {
        var protectedList = ProtectedMachineKey.CreateSecurity();

        Assert.True(ProtectedMachineKey.IsTrusted(protectedList, ProtectedMachineKey.CreateSecurity()));
        Assert.False(ProtectedMachineKey.IsTrusted(protectedList, UserWritable()));
        Assert.False(ProtectedMachineKey.IsTrusted(UserWritable(), protectedList));
        Assert.False(ProtectedMachineKey.IsTrusted(null, protectedList));
    }

    [Fact]
    public void MustEnforce_OverTheRealMachineHive_EvenWhenNotAsked()
    {
        // Reads the key's name only; nothing under HKLM is opened.
        Assert.True(ProtectedMachineKey.MustEnforce(Registry.LocalMachine, asked: false));
        Assert.False(ProtectedMachineKey.MustEnforce(Registry.CurrentUser, asked: false));
        Assert.True(ProtectedMachineKey.MustEnforce(Registry.CurrentUser, asked: true));
    }

    [Theory]
    [InlineData(@"SOFTWARE\SysManagerServiceStartupLedger", true)]
    [InlineData(@"SOFTWARE\SysManagerEnvironmentBackup", true)]
    [InlineData(@"Software\SysManagerEnvironmentBackup", true)]
    [InlineData(@"SOFTWARE\SysManager\Nested", false)]
    [InlineData(@"SOFTWARE", false)]
    [InlineData(@"SOFTWARE\", false)]
    [InlineData(@"SOFTWARE\ Padded", false)]
    [InlineData(@"SYSTEM\SysManager", false)]
    [InlineData(@"SysManager", false)]
    public void IsDirectlyUnderSoftware_TakesOneKeyRightBelowSoftwareOnly(string path, bool expected)
    {
        Assert.Equal(expected, ProtectedMachineKey.IsDirectlyUnderSoftware(path));
    }

    /// <summary>
    /// A key further down would sit under keys nobody checks, so it is refused before anything is created, with or
    /// without enforcement.
    /// </summary>
    [Fact]
    public void OpenOrCreate_ANestedPath_IsRefused_AndCreatesNothing()
    {
        var rootName = $@"Software\SysManagerTests\ProtectedKey\Machine_{Guid.NewGuid():N}";
        using var root = Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        try
        {
            Assert.Throws<ArgumentException>(
                () => ProtectedMachineKey.OpenOrCreate(root, @"SOFTWARE\SysManager\Nested", enforce: false, "test"));
            Assert.Null(root.OpenSubKey("SOFTWARE"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }
}
