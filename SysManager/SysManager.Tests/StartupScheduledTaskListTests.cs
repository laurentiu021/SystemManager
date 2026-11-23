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

    /// <summary>
    /// Adds a task to the redirected cache the way Task Scheduler lists one: its path and a trigger blob, and the
    /// description and author the task declares, when a test gives them.
    /// </summary>
    private void CacheTask(string uri, string? description = null, string? author = null)
    {
        using var task = _root.CreateSubKey(StartupService.TaskCachePath + @"\{" + Guid.NewGuid() + "}", writable: true)!;
        task.SetValue("Path", uri);
        task.SetValue("URI", uri);
        task.SetValue("Triggers", new byte[] { 0x17, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        if (description is not null) task.SetValue("Description", description);
        if (author is not null) task.SetValue("Author", author);
    }

    /// <summary>
    /// Writes the task's definition the way Task Scheduler stores one: UTF-16 with a BOM, in the task namespace, with
    /// <paramref name="actions"/> as what it runs.
    /// </summary>
    private void DefineTask(string uri, string triggers,
        string actions = "<Exec><Command>app.exe</Command></Exec>")
    {
        var path = Path.Combine(_tasksRoot, uri.TrimStart('\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
            + $"  <Triggers>{triggers}</Triggers>\r\n"
            + "  <Settings><Hidden>false</Hidden></Settings>\r\n"
            + $"  <Actions>{actions}</Actions>\r\n"
            + "</Task>\r\n",
            Encoding.Unicode);
    }

    /// <summary>
    /// A task row's command is the program the task runs, read from its definition, and the task's own description
    /// stays as the row's second line. The description used to be the command, so its first word picked a program.
    /// </summary>
    [Fact]
    public void ATaskRow_RunsTheProgramItsDefinitionNames_AndKeepsItsDescription()
    {
        var tool = Path.Combine(_tasksRoot, "Vendor Tools", "tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllBytes(tool, [0x4D, 0x5A]);
        CacheTask(@"\Vendor\Maintenance", description: "System maintenance for Contoso",
            author: "Microsoft Corporation");
        DefineTask(@"\Vendor\Maintenance", "<LogonTrigger />",
            $"<Exec><Command>{tool}</Command><Arguments>--run now</Arguments></Exec>"
            + "<Exec><Command>C:\\Other\\second.exe</Command></Exec>");
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results, _tasksRoot));

        var task = Assert.Single(results);
        Assert.Equal($"\"{tool}\" --run now", task.Command);
        Assert.Equal("System maintenance for Contoso", task.Description);
        // The publisher is the program's, which has none, not the author text the task declares.
        Assert.DoesNotContain("Microsoft", task.Publisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A task whose definition names no program to run keeps its description, and claims no program.</summary>
    [Fact]
    public void ATaskThatRunsNoProgram_KeepsItsDescription()
    {
        CacheTask(@"\Vendor\Handler", description: "Explorer helper for Contoso");
        DefineTask(@"\Vendor\Handler", "<LogonTrigger />",
            "<ComHandler><ClassId>{00000000-0000-0000-0000-000000000001}</ClassId></ComHandler>");
        List<StartupEntry> results = [];

        Assert.True(StartupService.ReadScheduledTasks(_root, results, _tasksRoot));
        StartupService.EnrichWithDescriptions(results);

        var task = Assert.Single(results);
        Assert.Equal("Explorer helper for Contoso", task.Command);
        Assert.Equal("", task.Safety);
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
