// SysManager · StartupScheduledTaskListTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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

    // The definitions the scan reads what runs each task from, standing in for System32\Tasks.
    private readonly string _tasksRoot = Path.Combine(Path.GetTempPath(), "SysManagerTests", "TaskDefinitions_" + Guid.NewGuid().ToString("N"));

    public StartupScheduledTaskListTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        if (Directory.Exists(_tasksRoot)) Directory.Delete(_tasksRoot, recursive: true);
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

    /// <summary>Adds a task to the redirected cache the way Task Scheduler lists one: its path and a trigger blob.</summary>
    private void CacheTask(string uri)
    {
        using var task = _root.CreateSubKey(StartupService.TaskCachePath + @"\{" + Guid.NewGuid() + "}", writable: true)!;
        task.SetValue("Path", uri);
        task.SetValue("URI", uri);
        task.SetValue("Triggers", new byte[] { 0x17, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }

    /// <summary>Writes the task's definition the way Task Scheduler stores one: UTF-16 with a BOM, in the task namespace.</summary>
    private void DefineTask(string uri, string triggers)
    {
        var path = Path.Combine(_tasksRoot, uri.TrimStart('\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
            + $"  <Triggers>{triggers}</Triggers>\r\n"
            + "  <Settings><Hidden>false</Hidden></Settings>\r\n"
            + "  <Actions><Exec><Command>app.exe</Command></Exec></Actions>\r\n"
            + "</Task>\r\n",
            Encoding.Unicode);
    }

    [Fact]
    public void SysManagersOwnMaintenanceTask_IsNotListed()
    {
        // It belongs to no other program, and runs on its schedule rather than at start-up (#2871).
        CacheTask(@"\" + MaintenanceSchedulerService.TaskNamePrefix + "S-1-5-21-1000-2000-3000-1001");
        CacheTask(@"\Vendor Updater");
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results, _tasksRoot));

        Assert.Equal("Vendor Updater", Assert.Single(results).Name);
    }

    [Fact]
    public void ATaskThatRunsAtSignIn_StartsWithWindows_AndOneOnASchedule_DoesNot()
    {
        CacheTask(@"\Vendor\Sign-in helper");
        DefineTask(@"\Vendor\Sign-in helper", "<LogonTrigger />");
        CacheTask(@"\Vendor\Hourly updater");
        DefineTask(@"\Vendor\Hourly updater", "<TimeTrigger><Repetition><Interval>PT1H</Interval></Repetition></TimeTrigger>");
        CacheTask(@"\Vendor\Unreadable");   // no definition to read
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results, _tasksRoot));

        var signIn = results.Single(e => e.Name == "Sign-in helper");
        Assert.True(signIn.StartsWithWindows);
        Assert.Equal("Enabled (at start-up)", signIn.StatusText);
        var hourly = results.Single(e => e.Name == "Hourly updater");
        Assert.False(hourly.StartsWithWindows);
        Assert.Equal("Enabled (scheduled)", hourly.StatusText);
        var unreadable = results.Single(e => e.Name == "Unreadable");
        Assert.False(unreadable.StartsWithWindows);   // not claimed without the definition
        Assert.True(unreadable.IsEnabled);           // the old assumption, kept
    }

    [Fact]
    public void NoCacheAtAll_IsNotARefusal()
    {
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results));
        Assert.Empty(results);
    }
}
