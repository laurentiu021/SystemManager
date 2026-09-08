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
    [InlineData(MaintenanceAction.PurgeStandby, "--purge-standby --silent")]
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
        var s = new MaintenanceSchedule(MaintenanceAction.PurgeStandby, MaintenanceFrequency.Weekly, 22, 30, DayOfWeek.Friday);
        Assert.Equal("Every Friday at 22:30", s.Summary);
    }

    [Fact]
    public void ActionLabel_IsHumanReadable()
    {
        Assert.Equal("Clean temporary files", new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 0, 0).ActionLabel);
        Assert.Equal("Purge standby memory", new MaintenanceSchedule(MaintenanceAction.PurgeStandby, MaintenanceFrequency.Daily, 0, 0).ActionLabel);
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

    // ── Every action is honestly named, end to end ────────────────────────

    /// <summary>
    /// Every declared <see cref="MaintenanceAction"/> has a plain-language label and a CLI argument
    /// string the parser actually recognises.
    /// </summary>
    /// <remarks>
    /// Enumerates the enum rather than listing values, so adding a third action fails here until it has
    /// both — which is the failure mode this is for. The existing rows are asserted individually above;
    /// this asserts the SET is complete, and those are different questions.
    /// <para><b>Why the round trip through the parser.</b> The defect behind #1524 was exactly a
    /// disagreement between the name and the behaviour: <c>MaintenanceAction.TrimRam</c> emitted
    /// <c>--trim-ram</c>, which <c>CliRunner</c> ran as a standby purge — Standby List Cleaner's
    /// operation under Performance Mode's name, with a different elevation requirement. Asserting the
    /// emitted verb resolves to a real command is what catches an action whose arguments fall through to
    /// <c>--help</c>, which is what an unrecognised flag does: the scheduled task would run nightly,
    /// exit 0, and do nothing.</para>
    /// </remarks>
    [Fact]
    public void EveryMaintenanceAction_HasALabelAndAVerbTheParserKnows()
    {
        var actions = Enum.GetValues<MaintenanceAction>();
        Assert.True(actions.Length >= 2, $"only {actions.Length} actions found — reading the wrong enum");

        foreach (var action in actions)
        {
            var label = MaintenanceSchedule.LabelFor(action);
            Assert.False(string.IsNullOrWhiteSpace(label), $"{action} has no label");
            Assert.NotEqual("Unknown", label);
            // Exact inequality, not "does not contain": a label may legitimately share a word with the
            // enum name. What must not happen is the label BEING the enum name, which is what the action
            // picker was effectively showing before it got an ItemTemplate.
            Assert.NotEqual(action.ToString(), label);

            var schedule = new MaintenanceSchedule(action, MaintenanceFrequency.Daily, 3, 0);
            var verb = schedule.CliArguments.Split(' ')[0];
            var parsed = CliRunner.Parse([verb]).Command;

            Assert.NotEqual(CliCommand.Unknown, parsed);
            Assert.NotEqual(CliCommand.None, parsed);
            Assert.NotEqual(CliCommand.Help, parsed);
        }
    }
}
