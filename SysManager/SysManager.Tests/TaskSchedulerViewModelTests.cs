// SysManager · TaskSchedulerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TaskSchedulerViewModel"/>'s cancellation and re-entrancy wiring (#1607).
/// <para>The service already accepted a <see cref="CancellationToken"/> on all three entry points and
/// the runner already translated cancellation; the view model simply passed none of them, had no
/// Cancel command, and left Refresh clickable during a scan. These tests pin the wiring — including
/// the one call that is deliberately NOT cancellable.</para>
/// <para>Nothing here sleeps or races: the fake runner returns a task that completes only when its
/// token is cancelled, so "cancel something in flight" is deterministic.</para>
/// </summary>
// Serialized: the Enable/Disable test swaps the static DialogService.Instance via DialogAnswer.
[Collection("ProcessWideStatics")]
public class TaskSchedulerViewModelTests
{
    // Distinctive fragments of the three scripts, so a recorded call can be attributed without
    // reaching into the service's private constants.
    private const string ListMarker = "Get-ScheduledTask | ForEach-Object";
    private const string SetEnabledMarker = "[bool]$Enabled";
    private const string InfoParams = "param([string]$Name, [string]$Path)";

    /// <summary>
    /// Records every runner invocation with the token it was given. When <c>hangUntilCancelled</c>,
    /// each call returns a task that never completes on its own — only cancellation ends it, which is
    /// what makes "in flight" a deterministic state rather than a timing window.
    /// </summary>
    private sealed class RunnerSpy
    {
        private readonly List<(string Script, CancellationToken Token)> _calls = [];

        public IPowerShellRunner Runner { get; }

        public RunnerSpy(bool hangUntilCancelled = false)
        {
            Runner = Substitute.For<IPowerShellRunner>();
            Runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
                            Arg.Any<CancellationToken>())
                  .Returns(ci =>
                  {
                      var script = (string)ci[0]!;
                      var ct = (CancellationToken)ci[2];
                      lock (_calls) _calls.Add((script, ct));

                      if (!hangUntilCancelled) return Task.FromResult(new Collection<PSObject>());

                      var tcs = new TaskCompletionSource<Collection<PSObject>>();
                      ct.Register(() => tcs.TrySetCanceled(ct));

                      // Bounded on purpose. Cancellation is what these tests actually assert, and it
                      // fires immediately — this fallback never runs on healthy code. It exists so a
                      // regression that drops the token (the very defect #1607 fixed) FAILS instead of
                      // hanging: without it, a token nothing can cancel means a task nothing can
                      // complete, and the suite waits forever on the one thing it meant to catch.
                      _ = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)
                              .ContinueWith(_ => tcs.TrySetResult([]),
                                            CancellationToken.None,
                                            TaskContinuationOptions.ExecuteSynchronously,
                                            TaskScheduler.Default);
                      return tcs.Task;
                  });
        }

        private List<(string Script, CancellationToken Token)> Snapshot()
        {
            lock (_calls) return [.. _calls];
        }

        public IReadOnlyList<CancellationToken> TokensFor(string marker) =>
            [.. Snapshot().Where(c => c.Script.Contains(marker, StringComparison.Ordinal)).Select(c => c.Token)];
    }

    private static TaskSchedulerViewModel NewVm(RunnerSpy spy) =>
        new(new TaskSchedulerService(spy.Runner));

    private static ScheduledTaskInfo Task1() =>
        new("Defrag", @"\Microsoft\Windows\Defrag\", "Ready", "Microsoft", "desc", TaskCategory.System, null, null);

    private static ScheduledTaskInfo Task2() =>
        new("Backup", @"\Custom\", "Ready", "me", "desc", TaskCategory.ThirdParty, null, null);

    // ── the scan is cancellable ────────────────────────────────────────────

    [Fact]
    public async Task Refresh_HandsTheServiceATokenThatCanActuallyBeCancelled()
    {
        // The defect was not a missing feature — the service and runner already supported this. The
        // view model called ListTasksAsync() with no argument, so the token was default, and
        // default.CanBeCanceled is false. That is what this asserts.
        var spy = new RunnerSpy();
        var vm = NewVm(spy);

        await vm.InitializationComplete;

        var token = Assert.Single(spy.TokensFor(ListMarker));
        Assert.True(token.CanBeCanceled,
            "the scan was handed a token nothing can ever cancel, so the Cancel button cannot work.");
    }

    [Fact]
    public async Task Cancel_StopsAScanInFlight_AndSaysSoInsteadOfLookingStuck()
    {
        var spy = new RunnerSpy(hangUntilCancelled: true);
        var vm = NewVm(spy);            // the initial scan starts and does not finish on its own

        Assert.True(vm.IsBusy);

        vm.CancelCommand.Execute(null);
        await vm.InitializationComplete;

        Assert.False(vm.IsBusy);
        Assert.Contains("Cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // Not left claiming it is still working.
        Assert.DoesNotContain("Loading", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_IsNotClickableDuringAScan()
    {
        // Both service calls run through one runspace-per-call runner, so a second scan started on
        // top of the first lets two invocations interleave. The sibling tabs each fixed this; this
        // one was left out of that migration.
        var spy = new RunnerSpy(hangUntilCancelled: true);
        var vm = NewVm(spy);

        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await vm.InitializationComplete;

        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    // ── Enable/Disable composes both gates ─────────────────────────────────

    [Fact]
    public async Task ToggleEnabled_NeedsASelectionAndAnIdleRunner()
    {
        var spy = new RunnerSpy();
        var vm = NewVm(spy);
        await vm.InitializationComplete;

        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));   // no selection

        vm.SelectedTask = Task1();
        Assert.True(vm.ToggleEnabledCommand.CanExecute(null));

        // The busy gate is ADDED, not swapped in for the selection gate.
        vm.IsBusy = true;
        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ToggleEnabledCommand.CanExecute(null));

        vm.SelectedTask = null;
        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));
    }

    [Fact]
    public async Task EnableDisable_IsDeliberatelyNotCancellable()
    {
        // The Enable/Disable script writes the new state and then reads it back. A cancel landing
        // between the two would leave the task toggled while the grid still showed the old value, so
        // this call is intentionally left un-cancellable. Pinned so a later "wire up every token"
        // sweep has to justify changing it rather than doing it by reflex.
        var spy = new RunnerSpy();
        using var dialog = new DialogAnswer(confirm: true);
        var vm = NewVm(spy);
        await vm.InitializationComplete;
        vm.SelectedTask = Task1();

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        var token = Assert.Single(spy.TokensFor(SetEnabledMarker));
        Assert.False(token.CanBeCanceled,
            "Enable/Disable became cancellable — a cancel between its write and its read-back leaves "
            + "the task toggled while the UI shows the old state.");
    }

    // ── per-selection run info supersedes rather than queues ───────────────

    [Fact]
    public async Task ANewSelection_CancelsThePreviousRunInfoQuery()
    {
        // Holding an arrow key down used to queue one PowerShell round-trip per row passed over, all
        // still running after the user had moved on.
        var spy = new RunnerSpy(hangUntilCancelled: true);
        var vm = NewVm(spy);
        vm.CancelCommand.Execute(null);        // end the initial scan so only run-info remains
        await vm.InitializationComplete;

        vm.SelectedTask = Task1();
        var first = Assert.Single(spy.TokensFor(InfoParams));
        Assert.False(first.IsCancellationRequested);

        vm.SelectedTask = Task2();

        var tokens = spy.TokensFor(InfoParams);
        Assert.Equal(2, tokens.Count);
        Assert.True(tokens[0].IsCancellationRequested,
            "the superseded run-info query is still running — arrow-keying the list queues one "
            + "PowerShell invocation per row.");
        Assert.False(tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_CancelsWorkStillInFlight()
    {
        var spy = new RunnerSpy(hangUntilCancelled: true);
        var vm = NewVm(spy);

        var scanToken = Assert.Single(spy.TokensFor(ListMarker));
        Assert.False(scanToken.IsCancellationRequested);

        vm.Dispose();

        Assert.True(scanToken.IsCancellationRequested,
            "closing the view left a PowerShell scan running with nothing waiting for it.");
        await vm.InitializationComplete;
    }
}
