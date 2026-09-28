// SysManager · StartupScheduledTaskListTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService.ReadScheduledTasks"/>: whether the scheduled tasks of other programs could be
/// listed at all (#2503).
/// </summary>
/// <remarks>
/// Windows' task cache grants read access to SYSTEM and Administrators only, so without elevation the read was refused
/// every time, logged at Debug, and the start-up list silently had no scheduled tasks. These run against a redirected
/// HKCU key standing in for the machine hive; the refusal is made real with a deny entry for this user, removed again
/// afterwards so the key can be deleted.
/// </remarks>
public sealed class StartupScheduledTaskListTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\StartupTasks_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public StartupScheduledTaskListTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { /* a leftover test key must never fail a test run */ }
    }

    [Fact]
    public void ACacheThatCannotBeRead_IsReportedAsNotListed()
    {
        _root.CreateSubKey(StartupService.TaskCachePath, writable: true)!.Dispose();
        using var cache = _root.OpenSubKey(StartupService.TaskCachePath, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(identity.User!,
            RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        var security = cache.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(deny);
        cache.SetAccessControl(security);
        List<StartupEntry> results = [];
        try
        {
            Assert.False(StartupService.ReadScheduledTasks(_root, results));
            Assert.Empty(results);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            cache.SetAccessControl(security);
        }

        Assert.True(StartupService.ReadScheduledTasks(_root, results));   // readable again
    }

    [Fact]
    public void AnEmptyCache_IsListed_WithNothingInIt()
    {
        _root.CreateSubKey(StartupService.TaskCachePath, writable: true)!.Dispose();
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results));
        Assert.Empty(results);
    }

    [Fact]
    public void NoCacheAtAll_IsNotARefusal()
    {
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results));
        Assert.Empty(results);
    }
}
