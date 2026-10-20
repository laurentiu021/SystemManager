// SysManager · StartupTaskStateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService.ReadTaskEnabled"/> (#2453). Every scheduled task used to be listed as enabled,
/// so a disabled task looked enabled, and disabling one here appeared to be undone by the next refresh. The state now
/// comes from the task's definition file, which these tests write to a temp folder standing in for
/// <c>System32\Tasks</c>.
/// </summary>
public sealed class StartupTaskStateTests : IDisposable
{
    private readonly string _tasksRoot = Path.Combine(Path.GetTempPath(), "SysManagerTests", "Tasks_" + Guid.NewGuid().ToString("N"));

    public StartupTaskStateTests() => Directory.CreateDirectory(_tasksRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tasksRoot)) Directory.Delete(_tasksRoot, recursive: true);
    }

    /// <summary>Writes a definition the way Task Scheduler stores one: UTF-16 with a BOM, in the task namespace.</summary>
    private void WriteTask(string relativePath, string settings, string triggers = "<LogonTrigger><Enabled>true</Enabled></LogonTrigger>")
    {
        var path = Path.Combine(_tasksRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            $"  <Triggers>{triggers}</Triggers>\r\n" +
            $"  <Settings>{settings}</Settings>\r\n" +
            "  <Actions><Exec><Command>app.exe</Command></Exec></Actions>\r\n" +
            "</Task>\r\n";
        File.WriteAllText(path, xml, Encoding.Unicode);
    }

    // ---------- what runs the task (#2871) ----------
    // Only a sign-in or start-up trigger makes a task start with Windows. The Dashboard's "Why is it slow?" counted every
    // enabled task as a program that does, hourly and daily updaters among them.

    [Theory]
    [InlineData("<LogonTrigger><Enabled>true</Enabled></LogonTrigger>", true)]
    [InlineData("<LogonTrigger />", true)]
    [InlineData("<BootTrigger />", true)]
    [InlineData("<CalendarTrigger><StartBoundary>2026-01-01T03:00:00</StartBoundary></CalendarTrigger>", false)]
    [InlineData("<TimeTrigger><Repetition><Interval>PT1H</Interval></Repetition></TimeTrigger>", false)]
    [InlineData("<LogonTrigger><Enabled>false</Enabled></LogonTrigger><CalendarTrigger />", false)]
    [InlineData("<CalendarTrigger /><LogonTrigger />", true)]
    [InlineData("", false)]
    public void ATask_StartsWithWindows_OnlyWhenAnEnabledSignInOrStartUpTriggerRunsIt(string triggers, bool expected)
    {
        WriteTask("Updater", "<Hidden>false</Hidden>", triggers);

        Assert.Equal(expected, StartupService.ReadTaskDefinition(_tasksRoot, @"\Updater")?.StartsWithWindows);
    }

    [Fact]
    public void ADisabledTask_KeepsWhatRunsIt_AndReadsAsDisabled()
    {
        WriteTask("Updater", "<Enabled>false</Enabled>", "<LogonTrigger />");

        Assert.Equal(new StartupService.TaskDefinition(Enabled: false, StartsWithWindows: true),
                     StartupService.ReadTaskDefinition(_tasksRoot, @"\Updater"));
        Assert.Equal("Disabled (scheduled)", StartupService.TaskStatus(enabled: false, startsWithWindows: true));
    }

    [Fact]
    public void ADisabledTask_ReadsAsDisabled()
    {
        WriteTask(@"Vendor\Updater", "<Enabled>false</Enabled><Hidden>false</Hidden>");

        Assert.False(StartupService.ReadTaskEnabled(_tasksRoot, @"\Vendor\Updater"));
    }

    [Fact]
    public void ATaskWithoutAnEnabledSetting_ReadsAsEnabled()
    {
        // Task Scheduler leaves the element out while the task is enabled.
        WriteTask("Updater", "<Hidden>false</Hidden>");

        Assert.True(StartupService.ReadTaskEnabled(_tasksRoot, @"\Updater"));
    }

    [Fact]
    public void ADisabledTrigger_DoesNotMakeTheTaskDisabled()
    {
        // Each trigger carries an Enabled of its own; only the one under Settings is the task's switch.
        WriteTask("Updater", "<Hidden>false</Hidden>", triggers: "<LogonTrigger><Enabled>false</Enabled></LogonTrigger>");

        Assert.True(StartupService.ReadTaskEnabled(_tasksRoot, @"\Updater"));
    }

    [Fact]
    public void AMissingDefinition_IsUnknown()
        => Assert.Null(StartupService.ReadTaskEnabled(_tasksRoot, @"\Vendor\NotThere"));

    [Fact]
    public void AMalformedDefinition_IsUnknown()
    {
        File.WriteAllText(Path.Combine(_tasksRoot, "Broken"), "<Task><Settings>", Encoding.Unicode);

        Assert.Null(StartupService.ReadTaskEnabled(_tasksRoot, @"\Broken"));
    }

    [Fact]
    public void APathThatLeavesTheTasksFolder_IsNeverRead()
    {
        // The task path comes from the registry. A definition outside the tasks folder, reachable with "..", must not
        // be read even when it exists and says the task is disabled.
        var outside = Path.Combine(Path.GetDirectoryName(_tasksRoot)!, "Outside_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "<Task><Settings><Enabled>false</Enabled></Settings></Task>", Encoding.Unicode);
        try
        {
            Assert.Null(StartupService.ReadTaskEnabled(_tasksRoot, @"\..\" + Path.GetFileName(outside)));
        }
        finally { File.Delete(outside); }
    }
}
