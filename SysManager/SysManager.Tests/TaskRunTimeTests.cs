// SysManager · TaskRunTimeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The run times Scheduled Maintenance and Task Scheduler read from Task Scheduler, and Task Scheduler's "never" among
/// them (#2748).
/// </summary>
public class TaskRunTimeTests
{
    private static PSObject Row(object? lastRun)
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("LastRunTime", lastRun));
        return row;
    }

    [Fact]
    public void TaskSchedulersNever_IsNoTime_AndSaysTheTaskHasNeverRun()
    {
        var row = Row(new DateTime(1999, 11, 30));

        Assert.Null(TaskRunTime.Read(row, "LastRunTime"));
        Assert.True(TaskRunTime.IsNever(row, "LastRunTime"));
    }

    [Theory]
    [InlineData(2000, 1, 1)]
    [InlineData(2026, 11, 2)]
    public void ARealRun_IsReadAsItIs(int year, int month, int day)
    {
        var row = Row(new DateTime(year, month, day, 3, 0, 0));

        Assert.Equal(new DateTime(year, month, day, 3, 0, 0), TaskRunTime.Read(row, "LastRunTime"));
        Assert.False(TaskRunTime.IsNever(row, "LastRunTime"));
    }

    [Fact]
    public void ATimeWrittenAsText_IsReadToo_AndTheNeverDateAsText_IsNever()
    {
        Assert.Equal(new DateTime(2026, 6, 29, 3, 0, 0), TaskRunTime.Read(Row("2026-06-29 03:00:00"), "LastRunTime"));
        Assert.True(TaskRunTime.IsNever(Row("1999-11-30 00:00:00"), "LastRunTime"));
    }

    [Fact]
    public void NoValue_IsNoTime_AndNotNever()
    {
        // A row without the figure says nothing about whether the task ran: "—", not "Never".
        Assert.Null(TaskRunTime.Read(Row(null), "LastRunTime"));
        Assert.False(TaskRunTime.IsNever(Row(null), "LastRunTime"));
        Assert.Null(TaskRunTime.Read(null, "LastRunTime"));
        Assert.False(TaskRunTime.IsNever(null, "LastRunTime"));
    }

    [Theory]
    [InlineData(false, false, "—")]
    [InlineData(false, true, "Never")]
    [InlineData(true, false, "2026-11-02 03:00")]
    public void ARunTime_IsShownAsBothTabsShowIt(bool hasTime, bool never, string shown)
        => Assert.Equal(shown, TaskRunTime.Display(hasTime ? new DateTime(2026, 11, 2, 3, 0, 0) : null, never));
}
