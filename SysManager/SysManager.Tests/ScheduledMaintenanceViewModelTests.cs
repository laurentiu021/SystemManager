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
    /// this test pins that, so the list above reads as a decision rather than an omission.</para>
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

        var beforeAction = vm.PendingSummary;
        vm.SelectedAction = vm.SelectedAction == MaintenanceAction.Cleanup
            ? MaintenanceAction.PurgeStandby
            : MaintenanceAction.Cleanup;
        Assert.Equal(beforeAction, vm.PendingSummary);
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
}
