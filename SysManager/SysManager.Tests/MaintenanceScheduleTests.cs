// SysManager · MaintenanceScheduleTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class MaintenanceScheduleTests
{
    // ── CliArguments: must map to the whitelisted CLI verbs, never free text ──

    [Theory]
    [InlineData(MaintenanceAction.Cleanup, "--cleanup --silent")]
    [InlineData(MaintenanceAction.TrimRam, "--trim-ram --silent")]
    public void CliArguments_MapToWhitelistedVerbs(MaintenanceAction action, string expected)
    {
        var s = new MaintenanceSchedule(action, MaintenanceFrequency.Daily, 3, 0);
        Assert.Equal(expected, s.CliArguments);
    }

    [Fact]
    public void CliArguments_AreAlwaysSilentFlags_NeverArbitraryText()
    {
        // Every action's argument string is one of the fixed, known-safe forms — this
        // guards the "no free-form input reaches the scheduler" invariant.
        foreach (MaintenanceAction action in Enum.GetValues<MaintenanceAction>())
        {
            var args = new MaintenanceSchedule(action, MaintenanceFrequency.Daily, 1, 0).CliArguments;
            Assert.Matches(@"^--[a-z-]+( --silent)?$", args);
        }
    }

    // ── Summary: plain-language schedule description ──────────────────────

    [Fact]
    public void Summary_Daily_OmitsDay()
    {
        var s = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 5);
        Assert.Equal("Every day at 03:05", s.Summary);
    }

    [Fact]
    public void Summary_Weekly_NamesDay()
    {
        var s = new MaintenanceSchedule(MaintenanceAction.TrimRam, MaintenanceFrequency.Weekly, 22, 30, DayOfWeek.Friday);
        Assert.Equal("Every Friday at 22:30", s.Summary);
    }

    [Fact]
    public void ActionLabel_IsHumanReadable()
    {
        Assert.Equal("Clean temporary files", new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 0, 0).ActionLabel);
        Assert.Equal("Purge standby memory", new MaintenanceSchedule(MaintenanceAction.TrimRam, MaintenanceFrequency.Daily, 0, 0).ActionLabel);
    }

    // ── DescribeResultCode: last-run status in plain language ─────────────

    [Theory]
    [InlineData(null, "Not run yet")]
    [InlineData(0, "Last run succeeded")]
    [InlineData(267009, "Currently running")]
    [InlineData(267011, "Not run yet")]
    public void DescribeResultCode_KnownCodes(int? code, string expected)
        => Assert.Equal(expected, MaintenanceSchedulerService.DescribeResultCode(code));

    [Fact]
    public void DescribeResultCode_UnknownCode_FallsBackToHex()
    {
        var msg = MaintenanceSchedulerService.DescribeResultCode(unchecked((int)0x80070005));
        Assert.Contains("0x80070005", msg);
    }

    // ── Task identity constants ───────────────────────────────────────────

    [Fact]
    public void TaskIdentity_IsSysManagerOwnedFolder()
    {
        Assert.Equal(@"\SysManager\", MaintenanceSchedulerService.TaskFolder);
        Assert.Equal("Scheduled Maintenance", MaintenanceSchedulerService.TaskName);
    }
}
