// SysManager · ScheduledMaintenanceViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ScheduledMaintenanceViewModel"/> — the power and idle conditions (#1578)
/// and the live summary that states them, from the bound property through to the parameters the
/// PowerShell seam actually receives.
/// </summary>
/// <remarks>
/// Serialized: the Save-gate test swaps the process-wide <c>DialogService.Instance</c>.
/// </remarks>
[Collection("ProcessWideStatics")]
public class ScheduledMaintenanceViewModelTests
{
    /// <summary>
    /// A view model over a runner that answers "no task registered", so the constructor's status read
    /// completes without touching the real Task Scheduler.
    /// </summary>
    private static (ScheduledMaintenanceViewModel vm, IPowerShellRunner ps) NewVm()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>());
        var vm = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(ps));
        // Awaiting the init task is what keeps this off the race the two guard tests in #2200 hit:
        // the constructor kicks off RefreshAsync, so asserting immediately reads a half-built VM.
        vm.InitializationComplete.GetAwaiter().GetResult();
        return (vm, ps);
    }

    [Fact]
    public void Defaults_RunOnBatteryOn_IdleOff()
    {
        // These two defaults ARE the fix for #1578: the tab inherited AllowStartIfOnBatteries = false
        // from New-ScheduledTaskSettingsSet, so an unplugged laptop never ran the schedule at all.
        var (vm, _) = NewVm();

        Assert.True(vm.RunOnBattery);
        Assert.False(vm.OnlyWhenIdle);
    }

    [Fact]
    public void PendingSummary_WithTheDefaults_NamesNoConditions()
    {
        var (vm, _) = NewVm();
        vm.SelectedFrequency = MaintenanceFrequency.Daily;
        vm.SelectedHour = 3;
        vm.SelectedMinute = 0;

        Assert.Equal("Every day at 03:00", vm.PendingSummary);
    }

    [Theory]
    [InlineData(false, false, "Every day at 03:00, only while plugged in")]
    [InlineData(true, true, "Every day at 03:00, only when you are not using the PC")]
    [InlineData(false, true, "Every day at 03:00, only while plugged in and only when you are not using the PC")]
    public void PendingSummary_TracksTheTickedConditions(bool onBattery, bool onlyIdle, string expected)
    {
        var (vm, _) = NewVm();
        vm.SelectedFrequency = MaintenanceFrequency.Daily;
        vm.SelectedHour = 3;
        vm.SelectedMinute = 0;

        vm.RunOnBattery = onBattery;
        vm.OnlyWhenIdle = onlyIdle;

        Assert.Equal(expected, vm.PendingSummary);
    }

    /// <summary>
    /// <see cref="ScheduledMaintenanceViewModel.PendingSummary"/> is a computed getter, so a binding only
    /// updates if each contributing setter raises it by name.
    /// </summary>
    /// <remarks>
    /// The live preview is the whole point of showing the conditions before the user commits, and the
    /// failure mode is silent: the getter is correct, the text on screen is stale, and every value-level
    /// test above still passes. Enumerating the setters rather than testing one keeps a seventh
    /// contributing field from being added without its notification.
    /// <para>Six fields, not seven: the summary answers <em>when</em>, so the chosen action is deliberately
    /// absent from it — the action's own label is shown in the picker and in the confirmation. The tail of
    /// this test pins that, so the list above reads as a decision rather than an omission. It used to switch
    /// the action and compare; with the cleanup the only action left (#2593), it looks for the label.</para>
    /// </remarks>
    [Fact]
    public void EveryFieldThePendingSummaryReads_RaisesItWhenChanged()
    {
        var (vm, _) = NewVm();
        vm.SelectedFrequency = MaintenanceFrequency.Daily;
        vm.SelectedHour = 3;
        vm.SelectedMinute = 0;
        vm.SelectedDay = DayOfWeek.Sunday;

        (string field, Action change)[] setters =
        [
            ("SelectedFrequency", () => vm.SelectedFrequency = MaintenanceFrequency.Weekly),
            ("SelectedDay", () => vm.SelectedDay = DayOfWeek.Wednesday),
            ("SelectedHour", () => vm.SelectedHour = 21),
            ("SelectedMinute", () => vm.SelectedMinute = 45),
            ("RunOnBattery", () => vm.RunOnBattery = false),
            ("OnlyWhenIdle", () => vm.OnlyWhenIdle = true),
        ];

        foreach (var (field, change) in setters)
        {
            var before = vm.PendingSummary;
            var raised = false;
            void Watch(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ScheduledMaintenanceViewModel.PendingSummary)) raised = true;
            }

            vm.PropertyChanged += Watch;
            try { change(); }
            finally { vm.PropertyChanged -= Watch; }

            // Both halves matter: a field that does not change the sentence would make the notification
            // assertion vacuous, and the notification is what the binding actually listens to.
            Assert.NotEqual(before, vm.PendingSummary);
            Assert.True(raised, $"changing {field} did not raise PendingSummary — the preview text goes stale");
        }

        Assert.DoesNotContain(MaintenanceSchedule.LabelFor(vm.SelectedAction), vm.PendingSummary,
                              StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveSchedule_SendsTheTickedConditionsToPowerShell(bool onBattery, bool onlyIdle)
    {
        // The end-to-end question a value test cannot answer: does the checkbox reach the scheduler, or
        // does it change a record nobody passes on? The runner returns no rows, so RegisterAsync reports
        // failure and the activity log stays untouched — the parameters are still recorded.
        var (vm, ps) = NewVm();
        vm.SelectedFrequency = MaintenanceFrequency.Daily;
        vm.RunOnBattery = onBattery;
        vm.OnlyWhenIdle = onlyIdle;

        var previous = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SaveScheduleCommand.Execute(null);
            await vm.SaveScheduleCommand.ExecutionTask!;
        }
        finally
        {
            DialogService.Instance = previous;
        }

        await ps.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<IDictionary<string, object?>?>(p =>
                p != null && p.ContainsKey("OnBattery") &&
                (bool)p["OnBattery"]! == onBattery &&
                (bool)p["OnlyIfIdle"]! == onlyIdle),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveSchedule_WhenTheUserDeclines_SendsNothing()
    {
        var (vm, ps) = NewVm();
        ps.ClearReceivedCalls(); // the constructor's status read is not what this asserts about

        var previous = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.SaveScheduleCommand.Execute(null);
            if (vm.SaveScheduleCommand.ExecutionTask is { } running) await running;
        }
        finally
        {
            DialogService.Instance = previous;
        }

        dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
        await ps.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
                                          Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A view model over a runner that answers "a task IS registered", with the next run Windows reports.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>MaintenanceSchedulerServiceTests.StatusRow</c>. The next run matters here rather than being
    /// filler: the replace confirmation names it, so it is the thing that tells the user WHICH schedule they
    /// are about to lose.
    /// </remarks>
    private static async Task<(ScheduledMaintenanceViewModel vm, IPowerShellRunner ps)> NewScheduledVmAsync(
        string arguments = "--cleanup --silent", int lastResult = 0)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { ScheduledRow(arguments, lastResult) });
        var vm = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(ps));
        await vm.InitializationComplete;
        return (vm, ps);
    }

    /// <summary>The row the status script returns for a registered task running <paramref name="arguments"/>.</summary>
    private static PSObject ScheduledRow(string arguments, int lastResult)
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("State", "Ready"));
        row.Properties.Add(new PSNoteProperty("LastRunTime", new DateTime(2026, 6, 29, 3, 0, 0)));
        row.Properties.Add(new PSNoteProperty("NextRunTime", new DateTime(2026, 6, 30, 3, 0, 0)));
        row.Properties.Add(new PSNoteProperty("LastTaskResult", lastResult));
        row.Properties.Add(new PSNoteProperty("Arguments", arguments));
        return row;
    }

    /// <summary>
    /// The one-schedule rule is on screen before the user acts, and it says which of the two situations they
    /// are in.
    /// </summary>
    /// <remarks>
    /// Nothing stated it. Saving does not add a second schedule, it overwrites the first, and someone who
    /// wanted a weekly cleanup AND a monthly standby purge would have set the second and silently lost the
    /// first (#1509).
    /// </remarks>
    [Fact]
    public void OneScheduleNote_WithNothingScheduled_SaysTheRuleWithoutWarning()
    {
        var (vm, _) = NewVm();

        Assert.False(vm.IsScheduled);
        Assert.Contains("one schedule at a time", vm.OneScheduleNote, StringComparison.Ordinal);
        Assert.DoesNotContain("replaces", vm.OneScheduleNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneScheduleNote_WithOneScheduled_SaysSavingReplacesIt()
    {
        var (vm, _) = await NewScheduledVmAsync();

        Assert.True(vm.IsScheduled);
        Assert.Contains("one schedule at a time", vm.OneScheduleNote, StringComparison.Ordinal);
        Assert.Contains("replaces the one above", vm.OneScheduleNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// The save confirmation asks the question that matches what the click will do.
    /// </summary>
    /// <remarks>
    /// One text served both cases and described only the first: "This creates a Windows scheduled task" was
    /// shown while about to overwrite an existing one, so the dialog whose whole job is to stop an unwanted
    /// change concealed which change it was. Asserted through <see cref="DialogAnswer"/> rather than by
    /// calling a helper directly, because what matters is the text that reaches the user from the real command
    /// path — and the surrounding tests' hand-rolled swap cannot read the wording at all.
    /// </remarks>
    [Fact]
    public async Task SaveSchedule_WithNothingScheduled_SaysItCreatesATask()
    {
        var (vm, _) = NewVm();

        using var dialog = new DialogAnswer(confirm: false);
        vm.SaveScheduleCommand.Execute(null);
        if (vm.SaveScheduleCommand.ExecutionTask is { } running) await running;

        var shown = Assert.Single(dialog.Messages);
        Assert.Contains("Schedule Maintenance — Confirm", shown, StringComparison.Ordinal);
        Assert.Contains("creates a Windows scheduled task", shown, StringComparison.Ordinal);
        // Even here it says the rule, so the limit is known before there is anything to lose.
        Assert.Contains("one schedule at a time", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSchedule_WithOneAlreadyScheduled_SaysItReplacesAndNamesTheNextRun()
    {
        var (vm, _) = await NewScheduledVmAsync();

        using var dialog = new DialogAnswer(confirm: false);
        vm.SaveScheduleCommand.Execute(null);
        if (vm.SaveScheduleCommand.ExecutionTask is { } running) await running;

        var shown = Assert.Single(dialog.Messages);
        Assert.Contains("Replace Schedule — Confirm", shown, StringComparison.Ordinal);
        Assert.Contains("REPLACES", shown, StringComparison.Ordinal);
        // The one detail available about the schedule being lost, and it comes from the same status read the
        // card above displays, so the dialog and the card cannot disagree.
        Assert.Contains("2026-06-30 03:00", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("creates a Windows scheduled task", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declining the replace confirmation leaves the existing schedule alone.
    /// </summary>
    /// <remarks>
    /// The counterpart to the wording tests: a dialog that says the right thing is worth nothing if No does
    /// not mean no. Asserted on the runner, because "the task is unchanged" is only observable as "no register
    /// script was sent".
    /// </remarks>
    [Fact]
    public async Task SaveSchedule_WhenTheUserDeclinesAReplacement_LeavesTheTaskAlone()
    {
        var (vm, ps) = await NewScheduledVmAsync();
        ps.ClearReceivedCalls(); // the constructor's status read is not what this asserts about

        using var dialog = new DialogAnswer(confirm: false);
        vm.SaveScheduleCommand.Execute(null);
        if (vm.SaveScheduleCommand.ExecutionTask is { } running) await running;

        Assert.Equal(1, dialog.Calls);
        await ps.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
                                          Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MissedRunsWarning_IsEmptyWhenNoTaskIsRegistered()
    {
        // Empty string, not the record's null: the view binds Visibility to this, and FlexVis collapses
        // on empty. A "0 missed runs" or a stray placeholder here would show an empty warning card.
        var (vm, _) = NewVm();

        Assert.False(vm.IsScheduled);
        Assert.Equal("", vm.MissedRunsWarning);
    }

    [Fact]
    public async Task MissedRunsWarning_SurfacesWindowsCount_WhenTheTaskExists()
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("State", "Ready"));
        row.Properties.Add(new PSNoteProperty("LastTaskResult", 0));
        row.Properties.Add(new PSNoteProperty("MissedRunsCount", 3u)); // CIM hands this back unsigned

        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { row });
        var vm = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(ps));
        await vm.InitializationComplete;

        Assert.True(vm.IsScheduled);
        Assert.StartsWith("3 scheduled runs did not happen", vm.MissedRunsWarning, StringComparison.Ordinal);
    }

    // ── a schedule that purges standby memory fails every run (#2593) ──────
    // The task runs without administrator rights and a purge needs them. Its only trace was LAST RESULT reading
    // "Last run returned 0x00000001".

    [Fact]
    public async Task AScheduleThatPurgesStandby_IsCalledOutWithWhatToDoInstead()
    {
        var (vm, _) = await NewScheduledVmAsync(arguments: "--purge-standby --silent", lastResult: 1);

        Assert.True(vm.IsScheduled);
        Assert.Contains("every run fails", vm.StandbyPurgeWarning, StringComparison.Ordinal);
        Assert.Contains("Standby List Cleaner", vm.StandbyPurgeWarning, StringComparison.Ordinal);
        Assert.Equal("Last run failed", vm.LastResult);
    }

    [Fact]
    public async Task ACleanupSchedule_CarriesNoPurgeWarning()
    {
        var (vm, _) = await NewScheduledVmAsync(arguments: "--cleanup --silent");

        Assert.True(vm.IsScheduled);
        Assert.Equal("", vm.StandbyPurgeWarning);
    }

    /// <summary>
    /// Saving over a schedule that purges standby memory replaces it with a cleanup, and the warning goes with it.
    /// </summary>
    /// <remarks>
    /// The warning says that saving replaces the purge with a temporary-file cleanup, so this holds it to that:
    /// the command line the save sends, and the card once Windows reports the new task.
    /// </remarks>
    [Fact]
    public async Task SavingOverAPurgeSchedule_ReplacesItWithACleanup_AndClearsTheWarning()
    {
        var registered = false;
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(call =>
          {
              if (call.ArgAt<string>(0).Contains("Register-ScheduledTask", StringComparison.Ordinal))
              {
                  registered = true;
                  var state = new PSObject();
                  state.Properties.Add(new PSNoteProperty("State", "Ready"));
                  return Task.FromResult(new Collection<PSObject> { state });
              }
              return Task.FromResult(new Collection<PSObject>
              {
                  registered ? ScheduledRow("--cleanup --silent", 0) : ScheduledRow("--purge-standby --silent", 1),
              });
          });
        var vm = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(ps));
        await vm.InitializationComplete;
        Assert.NotEqual("", vm.StandbyPurgeWarning);   // the starting point, or the end state below proves nothing

        using var activity = new ActivityLogScope();
        using var dialog = new DialogAnswer(confirm: true);
        await vm.SaveScheduleCommand.ExecuteAsync(null);

        await ps.Received(1).RunAsync(
            Arg.Is<string>(s => s.Contains("Register-ScheduledTask", StringComparison.Ordinal)),
            Arg.Is<IDictionary<string, object?>?>(p => p != null && (string)p["Args"]! == "--cleanup --silent"),
            Arg.Any<CancellationToken>());
        Assert.Equal("", vm.StandbyPurgeWarning);
    }

    // ── a read that fails is not "nothing scheduled" (#2487) ──────────────
    // The service returned "not registered" for a failed read, so the card said "No maintenance is scheduled yet."
    // and Save offered to create a schedule it might be about to replace.

    private static ScheduledMaintenanceViewModel VmWhoseStatusReadFails()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new RuntimeException("The Task Scheduler service did not answer."));
        var vm = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(ps));
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void AFailedStatusRead_SaysSo_InsteadOfNothingScheduled()
    {
        var vm = VmWhoseStatusReadFails();

        Assert.True(vm.StatusUnknown);
        Assert.False(vm.IsScheduled);
        Assert.Equal("The maintenance schedule could not be read.", vm.CurrentSummary);
        Assert.Contains("replaces any schedule it has already set", vm.OneScheduleNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSchedule_AfterAFailedRead_SaysItMayReplaceOne()
    {
        var vm = VmWhoseStatusReadFails();
        using var dialog = new DialogAnswer(confirm: false);

        await vm.SaveScheduleCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.Contains("could not read whether a schedule is already registered", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("This creates a Windows scheduled task", shown, StringComparison.Ordinal);
    }
}
