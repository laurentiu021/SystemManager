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
    // ── Power and idle policy (#1578): the conditions that decide whether the schedule happens at all ──

    [Fact]
    public void ByDefault_TheScheduleMayRunOnBatteryAndDoesNotWaitForIdle()
    {
        // The defaults ARE the fix. New-ScheduledTaskSettingsSet leaves AllowStartIfOnBatteries false, so
        // inheriting it meant an unplugged laptop never ran the schedule at all.
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 0);

        Assert.True(schedule.RunOnBattery);
        Assert.False(schedule.OnlyWhenIdle);
    }

    [Fact]
    public void Summary_WithTheDefaults_StatesTheTimeAndNoConditions()
    {
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 0);

        Assert.Equal("Every day at 03:00", schedule.Summary);
    }

    [Theory]
    [InlineData(false, false, "Every day at 03:00, only while plugged in")]
    [InlineData(true, true, "Every day at 03:00, only when you are not using the PC")]
    [InlineData(false, true, "Every day at 03:00, only while plugged in and only when you are not using the PC")]
    public void Summary_NamesEveryConditionThatCanStopTheRun(bool onBattery, bool onlyIdle, string expected)
    {
        // Only the RESTRICTIONS are named. "Even on battery" is the default and adds nothing to plan around;
        // "only while plugged in" is the sentence that explains a run that never came.
        var schedule = new MaintenanceSchedule(
            MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 0,
            RunOnBattery: onBattery, OnlyWhenIdle: onlyIdle);

        Assert.Equal(expected, schedule.Summary);
    }

    [Fact]
    public void Summary_KeepsTheWeeklyDay_WithConditionsAppended()
    {
        var schedule = new MaintenanceSchedule(
            MaintenanceAction.Cleanup, MaintenanceFrequency.Weekly, 3, 30, DayOfWeek.Sunday,
            RunOnBattery: false);

        Assert.Equal("Every Sunday at 03:30, only while plugged in", schedule.Summary);
    }

    // ── The policy has to REACH the script, not just the record ──

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RegisterParameters_CarryThePowerAndIdlePolicy(bool onBattery, bool onlyIdle)
    {
        var schedule = new MaintenanceSchedule(
            MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 0,
            RunOnBattery: onBattery, OnlyWhenIdle: onlyIdle);

        var parameters = MaintenanceSchedulerService.RegisterParameters(schedule, @"C:\x\SysManager.exe");

        Assert.Equal(onBattery, parameters["OnBattery"]);
        Assert.Equal(onlyIdle, parameters["OnlyIfIdle"]);
    }

    [Fact]
    public void RegisterScript_AppliesTheBatteryFlagsOnlyWhenAsked()
    {
        // The flags must sit INSIDE the conditional, not in the unconditional settings block — otherwise
        // unticking "Run even when on battery" would change the record and nothing else.
        var script = CodeOnly(MaintenanceSchedulerService.RegisterScript);

        Assert.Contains("if ($OnBattery) {", script, StringComparison.Ordinal);
        Assert.Contains("$settingsArgs['AllowStartIfOnBatteries'] = $true", script, StringComparison.Ordinal);
        Assert.Contains("$settingsArgs['DontStopIfGoingOnBatteries'] = $true", script, StringComparison.Ordinal);
        Assert.Contains("if ($OnlyIfIdle) { $settingsArgs['RunOnlyIfIdle'] = $true }", script,
                        StringComparison.Ordinal);

        // Each flag appears exactly once, and after its own guard rather than before any of them.
        foreach (var flag in new[] { "AllowStartIfOnBatteries", "RunOnlyIfIdle" })
        {
            var guard = flag == "RunOnlyIfIdle" ? "if ($OnlyIfIdle)" : "if ($OnBattery)";
            Assert.True(script.IndexOf(guard, StringComparison.Ordinal)
                        < script.IndexOf(flag, StringComparison.Ordinal),
                        $"{flag} must appear after {guard}, or it is applied unconditionally");
        }
    }

    [Fact]
    public void RegisterScript_BoundsTheRunWithoutAUtilityCmdlet()
    {
        // A bounded ExecutionTimeLimit so a wedged maintenance run cannot sit in the scheduler forever, and
        // [TimeSpan]::FromHours rather than New-TimeSpan: the runner's runspace is CreateDefault2, which loads
        // Microsoft.PowerShell.Core only, so a Utility cmdlet here is not something to depend on.
        var script = CodeOnly(MaintenanceSchedulerService.RegisterScript);

        Assert.Contains("ExecutionTimeLimit = [TimeSpan]::FromHours(1)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("New-TimeSpan", script, StringComparison.Ordinal);
    }

    /// <summary>The script with its <c>#</c> comments removed, so an assertion cannot match its own prose.</summary>
    /// <remarks>
    /// Both script assertions above failed on their first run for exactly that reason. The script's comment
    /// explains that "AllowStartIfOnBatteries defaults to $FALSE" and that the limit uses
    /// "[TimeSpan]::FromHours, not New-TimeSpan" — so a DoesNotContain on New-TimeSpan found the word in the
    /// sentence saying not to use it, and an ordering check found AllowStartIfOnBatteries in the comment
    /// ABOVE its own guard. Match the code shape, never the documentation of it.
    /// </remarks>
    private static string CodeOnly(string script) => string.Join(
        Environment.NewLine,
        script.Split('\n').Where(line => !line.TrimStart().StartsWith("#", StringComparison.Ordinal)));

    // ── Missed runs: the only signal Windows gives for "the conditions blocked it" ──

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MissedRunsWarning_IsSilentWhenNothingWasMissed(int? missed)
    {
        // Not "0 missed runs". A reassuring zero is noise on a tab that already shows three status fields.
        var status = new MaintenanceStatus(true, "Ready", null, null, "Last run succeeded", missed);

        Assert.Null(status.MissedRunsWarning);
    }

    [Theory]
    [InlineData(1, "1 scheduled run did not happen")]
    [InlineData(4, "4 scheduled runs did not happen")]
    public void MissedRunsWarning_CountsInThePlainSingularOrPlural(int missed, string expected)
    {
        var status = new MaintenanceStatus(true, "Ready", null, null, "Last run succeeded", missed);

        Assert.NotNull(status.MissedRunsWarning);
        Assert.StartsWith(expected, status.MissedRunsWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void NotRegistered_IsTheShapeEveryFailurePathReturns()
    {
        var status = MaintenanceStatus.NotRegistered;

        Assert.False(status.Exists);
        Assert.Null(status.State);
        Assert.Null(status.LastRun);
        Assert.Null(status.NextRun);
        Assert.Null(status.LastResultDescription);
        Assert.Null(status.MissedRuns);
        Assert.Null(status.MissedRunsWarning);
    }

}
