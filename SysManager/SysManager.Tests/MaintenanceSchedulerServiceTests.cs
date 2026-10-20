// SysManager · MaintenanceSchedulerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class MaintenanceSchedulerServiceTests
{
    // Made-up accounts, so no test depends on the account running it.
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private const string OtherSid = "S-1-5-21-1000-2000-3000-1002";
    private const string OwnTask = MaintenanceSchedulerService.TaskNamePrefix + Sid;

    private static PSObject StateRow(string state)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("State", state));
        return o;
    }

    private static PSObject StatusRow(string state, object? lastResult)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("State", state));
        o.Properties.Add(new PSNoteProperty("LastRunTime", new DateTime(2026, 6, 29, 3, 0, 0)));
        o.Properties.Add(new PSNoteProperty("NextRunTime", new DateTime(2026, 6, 30, 3, 0, 0)));
        o.Properties.Add(new PSNoteProperty("LastTaskResult", lastResult));
        return o;
    }

    private static (MaintenanceSchedulerService svc, IPowerShellRunner ps) NewService(params PSObject[] rows)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>(rows.ToList()));
        return (new MaintenanceSchedulerService(ps, Sid), ps);
    }

    // ── RegisterAsync: only allowlisted args reach PowerShell (security invariant) ──

    [Fact]
    public async Task RegisterAsync_PassesAllowlistedArgs_NeverFreeText()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Weekly, 3, 30, DayOfWeek.Sunday);

        var ok = await svc.RegisterAsync(schedule, exePath: @"C:\Apps\SysManager.exe");

        Assert.True(ok);
        await ps.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<IDictionary<string, object?>?>(p =>
                p != null &&
                (string)p["Exe"]! == @"C:\Apps\SysManager.exe" &&
                (string)p["Arguments"]! == "--cleanup --silent" &&     // allowlisted, never arbitrary
                (string)p["Folder"]! == MaintenanceSchedulerService.TaskFolder &&
                (string)p["Name"]! == OwnTask &&
                (string)p["UserId"]! == Sid &&
                (bool)p["Daily"]! == false &&
                (string)p["At"]! == "03:30" &&
                (string)p["DayOfWeek"]! == "Sunday"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_DailySchedule_SetsDailyTrue()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 9, 0);
        await svc.RegisterAsync(schedule, exePath: @"C:\x.exe");
        await ps.Received(1).RunAsync(Arg.Any<string>(),
            Arg.Is<IDictionary<string, object?>?>(p => (bool)p!["Daily"]! && (string)p["At"]! == "09:00"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_NullExePath_ReturnsFalse_AndDoesNotRun()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        var ok = await svc.RegisterAsync(schedule, exePath: "");
        Assert.False(ok);
        await ps.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_EmptyResults_ReturnsFalse()
    {
        var (svc, _) = NewService(); // no rows back
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        Assert.False(await svc.RegisterAsync(schedule, exePath: @"C:\x.exe"));
    }

    [Fact]
    public async Task RegisterAsync_PowerShellThrows_ReturnsFalse()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new RuntimeException("denied"));
        var svc = new MaintenanceSchedulerService(ps);
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        Assert.False(await svc.RegisterAsync(schedule, exePath: @"C:\x.exe"));
    }

    // ── GetStatusAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NoRows_ReturnsExistsFalse()
    {
        var (svc, _) = NewService();
        var status = await svc.GetStatusAsync();
        Assert.NotNull(status);
        Assert.False(status.Exists);
    }

    [Fact]
    public async Task GetStatusAsync_MapsStateRunsAndResult()
    {
        var (svc, _) = NewService(StatusRow("Ready", 0));
        var status = await svc.GetStatusAsync();
        Assert.NotNull(status);
        Assert.True(status.Exists);
        Assert.Equal("Ready", status.State);
        Assert.Equal(new DateTime(2026, 6, 29, 3, 0, 0), status.LastRun);
        Assert.Equal(new DateTime(2026, 6, 30, 3, 0, 0), status.NextRun);
        Assert.Equal("Last run succeeded", status.LastResultDescription);
    }

    [Fact]
    public async Task GetStatusAsync_MapsUnsignedFailureResultFromRemoting()
    {
        var (svc, _) = NewService(
            StatusRow("Ready", unchecked((uint)0x80070002)));

        var status = await svc.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Last run failed (file not found)", status.LastResultDescription);
    }

    /// <summary>
    /// SysManager's own error exit code reads as a failure in words, whichever type the host returns it as.
    /// </summary>
    /// <remarks>
    /// It read "Last run returned 0x00000001", and that 1 was every scheduled standby purge failing for want of
    /// administrator rights (#2593).
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(1u)]
    [InlineData("1")]
    public async Task GetStatusAsync_DescribesAFailedRunInWords(object lastResult)
    {
        var (svc, _) = NewService(StatusRow("Ready", lastResult));

        var status = await svc.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Last run failed", status.LastResultDescription);
    }

    /// <summary>
    /// A schedule whose command line purges standby memory is flagged, read the way the CLI reads it when the task
    /// runs.
    /// </summary>
    /// <remarks>
    /// The task runs without administrator rights and a purge needs them, so every run of such a schedule fails
    /// (#2593). The old name counts too: a schedule saved before #1524 runs <c>--trim-ram</c>. A row without the
    /// property is a task this cannot read the command line of, and is not flagged.
    /// </remarks>
    [Theory]
    [InlineData("--purge-standby --silent", true)]
    [InlineData("--trim-ram --silent", true)]
    [InlineData("--cleanup --silent", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task GetStatusAsync_FlagsAScheduleThatPurgesStandby(string? arguments, bool purges)
    {
        var row = StatusRow("Ready", 0);
        if (arguments is not null) row.Properties.Add(new PSNoteProperty("Arguments", arguments));
        var (svc, _) = NewService(row);

        var status = await svc.GetStatusAsync();

        Assert.NotNull(status);
        Assert.True(status.Exists);
        Assert.Equal(purges, status.PurgesStandby);
    }

    /// <summary>
    /// <c>NumberOfMissedRuns</c> survives the trip through <see cref="PSObject"/>, whichever integral type
    /// the host hands it back as.
    /// </summary>
    /// <remarks>
    /// The count comes back <c>uint</c> from the CIM layer but <c>int</c> through some hosts — the same split
    /// <c>LastTaskResult</c> already has to handle — so a single-type cast would read null on whichever host
    /// disagreed, and the missed-runs warning (#1578) would just never appear. Absent is its own row because
    /// a task that has never been evaluated has no count at all, and that must read as "nothing to report"
    /// rather than zero.
    /// </remarks>
    [Fact]
    public async Task GetStatusAsync_ReadsMissedRuns_FromEitherIntegralType()
    {
        foreach (object? raw in new object?[] { 4, 4u, 4L, "4" })
        {
            var row = StatusRow("Ready", 0);
            row.Properties.Add(new PSNoteProperty("MissedRunsCount", raw));
            var (svc, _) = NewService(row);

            var status = await svc.GetStatusAsync();

            Assert.NotNull(status);
            Assert.Equal(4, status.MissedRuns);
            Assert.StartsWith("4 scheduled runs did not happen", status.MissedRunsWarning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetStatusAsync_NoMissedRunsProperty_ReportsNothing()
    {
        var (svc, _) = NewService(StatusRow("Ready", 0)); // the row has no MissedRunsCount at all

        var status = await svc.GetStatusAsync();

        Assert.NotNull(status);
        Assert.True(status.Exists);
        Assert.Null(status.MissedRuns);
        Assert.Null(status.MissedRunsWarning);
    }

    [Fact]
    public async Task GetStatusAsync_PowerShellThrows_ReportsTheReadFailed_NotThatNothingIsScheduled()
    {
        // Inverted deliberately (#2487). This used to assert Exists == false, which pinned the defect: a failed read
        // told the tab that nothing was scheduled, and Save then offered to create a schedule it might replace.
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new RuntimeException("boom"));
        var svc = new MaintenanceSchedulerService(ps);
        Assert.Null(await svc.GetStatusAsync());
    }

    // ── RemoveAsync: removed only when Windows then says there is no task ──

    [Fact]
    public async Task RemoveAsync_WhenWindowsThenSaysThereIsNoTask_ReportsItRemoved()
    {
        var (svc, _) = NewService();   // both scripts answer with nothing: the task is gone
        Assert.True(await svc.RemoveAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhenTheReadBackFails_DoesNotClaimTheTaskIsGone()
    {
        // #2487. The read-back used to count a failed read as "no task", so a removal it could not confirm was
        // reported as done.
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(ci => ((string)ci[0]!).Contains("Unregister-ScheduledTask", StringComparison.Ordinal)
              ? Task.FromResult(new Collection<PSObject>())
              : Task.FromException<Collection<PSObject>>(new RuntimeException("The Task Scheduler service did not answer.")));

        Assert.False(await new MaintenanceSchedulerService(ps).RemoveAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhenWindowsRefuses_SaysItWasNotRemoved()
    {
        // The remove script stops on a refusal now, so the reason reaches the log the tab points to; the read-back
        // still decides what the tab says.
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(ci => ((string)ci[0]!).Contains("Unregister-ScheduledTask", StringComparison.Ordinal)
              ? Task.FromException<Collection<PSObject>>(new RuntimeException("Access is denied."))
              : Task.FromResult(new Collection<PSObject> { PSObject.AsPSObject(new { State = "Ready" }) }));

        Assert.False(await new MaintenanceSchedulerService(ps).RemoveAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhenTheRefusedTaskTurnsOutToBeGone_CountsItRemoved()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(ci => ((string)ci[0]!).Contains("Unregister-ScheduledTask", StringComparison.Ordinal)
              ? Task.FromException<Collection<PSObject>>(new RuntimeException("The task was not found."))
              : Task.FromResult(new Collection<PSObject>()));

        Assert.True(await new MaintenanceSchedulerService(ps).RemoveAsync());
    }

    [Fact]
    public void NeitherScriptHidesAnError()
    {
        // #2487 took SilentlyContinue off the task read; the run-information read and the removal kept it, so a
        // failed read showed "Not run yet" and a refused removal left nothing in the log.
        Assert.Contains("Get-ScheduledTaskInfo", MaintenanceSchedulerService.StatusScript, StringComparison.Ordinal);
        Assert.Contains("Unregister-ScheduledTask", MaintenanceSchedulerService.RemoveScript, StringComparison.Ordinal);
        foreach (var script in new[] { MaintenanceSchedulerService.StatusScript, MaintenanceSchedulerService.RemoveScript })
            Assert.DoesNotContain("SilentlyContinue", script, StringComparison.OrdinalIgnoreCase);
    }

    // ── One task per account ──────────────────────────────────────────────
    // There was one task at a fixed path for the whole PC. On a PC with two accounts each tab would have shown the
    // other account's schedule as its own, and Save or Remove run as administrator would have replaced or deleted it.

    private static MaintenanceSchedule Daily() => new(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 3, 0);

    /// <summary>
    /// A service for the account with <paramref name="sid"/> over <paramref name="tasks"/>, keyed by folder and name,
    /// which the scripts change as Windows would: the status script reads one, the register script adds or replaces
    /// one, the remove script deletes one. Two services over the same tasks are two accounts on one PC.
    /// </summary>
    private static MaintenanceSchedulerService Account(string sid, Dictionary<string, PSObject> tasks)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(call =>
          {
              var script = call.ArgAt<string>(0);
              var p = call.ArgAt<IDictionary<string, object?>?>(1)!;
              var key = (string)p["Folder"]! + (string)p["Name"]!;
              if (script == MaintenanceSchedulerService.RegisterScript)
              {
                  var row = StatusRow("Ready", 0);
                  row.Properties.Add(new PSNoteProperty("RunsAs", (string)p["UserId"]!));
                  tasks[key] = row;
                  return Task.FromResult(new Collection<PSObject> { StateRow("Ready") });
              }
              if (script == MaintenanceSchedulerService.RemoveScript) tasks.Remove(key);
              return Task.FromResult(script == MaintenanceSchedulerService.StatusScript && tasks.TryGetValue(key, out var found)
                  ? new Collection<PSObject> { found }
                  : new Collection<PSObject>());
          });
        return new MaintenanceSchedulerService(ps, sid);
    }

    [Fact]
    public async Task AnotherAccountsSchedule_IsNotThisAccounts()
    {
        var tasks = new Dictionary<string, PSObject>();
        Assert.True(await Account(OtherSid, tasks).RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));

        var status = await Account(Sid, tasks).GetStatusAsync();

        Assert.NotNull(status);   // read, and nothing of this account's is there: not the "could not be read" null
        Assert.False(status.Exists);
    }

    [Fact]
    public async Task SavingBesideAnotherAccountsSchedule_LeavesThatOneAsItWas()
    {
        var tasks = new Dictionary<string, PSObject>();
        Assert.True(await Account(OtherSid, tasks).RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));

        Assert.True(await Account(Sid, tasks).RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));

        Assert.Equal(2, tasks.Count);
        Assert.Equal([Sid, OtherSid], tasks.Values.Select(t => (string)t.Properties["RunsAs"].Value).Order());
    }

    [Fact]
    public async Task Removing_LeavesAnotherAccountsScheduleAlone()
    {
        var tasks = new Dictionary<string, PSObject>();
        var other = Account(OtherSid, tasks);
        var mine = Account(Sid, tasks);
        Assert.True(await other.RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));
        Assert.True(await mine.RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));

        Assert.True(await mine.RemoveAsync());

        Assert.False((await mine.GetStatusAsync())!.Exists);
        Assert.True((await other.GetStatusAsync())!.Exists);   // the other account's schedule still runs
    }

    [Fact]
    public async Task WithoutAnAccount_NothingIsReadOrRegistered()
    {
        // Windows did not say which account this is, so there is no task name to read, save or remove.
        var ps = Substitute.For<IPowerShellRunner>();
        var svc = new MaintenanceSchedulerService(ps, accountSid: null);

        Assert.Null(svc.TaskName);
        Assert.Null(await svc.GetStatusAsync());
        Assert.False(await svc.RegisterAsync(Daily(), exePath: @"C:\Apps\SysManager.exe"));
        Assert.False(await svc.RemoveAsync());
        await ps.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ThePublicConstructor_SchedulesForTheAccountSysManagerRunsAs()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

        var svc = new MaintenanceSchedulerService(Substitute.For<IPowerShellRunner>());

        Assert.Equal(MaintenanceSchedulerService.TaskNamePrefix + identity.User!.Value, svc.TaskName);
    }
}
