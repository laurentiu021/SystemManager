// SysManager · LogsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class LogsViewModelTests
{
    private static bool InvokeFilter(LogsViewModel vm, FriendlyEventEntry e)
    {
        var m = typeof(LogsViewModel).GetMethod("EntryFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)m.Invoke(vm, new object[] { e })!;
    }

    private static FriendlyEventEntry Make(EventSeverity sev, string msg = "", string provider = "X", int id = 1)
        => new()
        {
            Severity = sev,
            Message = msg,
            FullMessage = msg,
            ProviderName = provider,
            EventId = id
        };

    [Fact]
    public void Defaults_ShowCriticalErrorWarning_HideInfoVerbose()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        Assert.True(vm.ShowCritical);
        Assert.True(vm.ShowError);
        Assert.True(vm.ShowWarning);
        Assert.False(vm.ShowInfo);
        Assert.False(vm.ShowVerbose);
    }

    [Fact]
    public void Filter_BySeverity_TogglesEntries()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        var err = Make(EventSeverity.Error);
        var info = Make(EventSeverity.Info);

        Assert.True(InvokeFilter(vm, err));
        Assert.False(InvokeFilter(vm, info)); // info off by default

        vm.ShowInfo = true;
        Assert.True(InvokeFilter(vm, info));

        vm.ShowError = false;
        Assert.False(InvokeFilter(vm, err));
    }

    [Fact]
    public void Filter_Search_MatchesMessageProviderAndEventId()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        var e = Make(EventSeverity.Error, "Disk I/O timeout at sector 500", "disk", 7);

        vm.FilterText = "sector";
        Assert.True(InvokeFilter(vm, e));

        vm.FilterText = "DISK"; // case-insensitive by provider
        Assert.True(InvokeFilter(vm, e));

        vm.FilterText = "7"; // by event id
        Assert.True(InvokeFilter(vm, e));

        vm.FilterText = "nothing-matches-this";
        Assert.False(InvokeFilter(vm, e));
    }

    [Fact]
    public void Filter_EmptySearch_MatchesWhenSeverityAllowed()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        var e = Make(EventSeverity.Warning);
        vm.FilterText = "";
        Assert.True(InvokeFilter(vm, e));
        vm.FilterText = "   ";
        Assert.True(InvokeFilter(vm, e));
    }

    [Fact]
    public void TimeRanges_DefaultIs24Hours()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        Assert.Equal("Last 24 hours", vm.SelectedTimeRange);
        Assert.Contains("Last hour", vm.TimeRanges);
        Assert.Contains("All", vm.TimeRanges);
    }

    [Fact]
    public void AvailableLogs_ContainsStandardWindowsLogs()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        Assert.Contains("System", vm.AvailableLogs);
        Assert.Contains("Application", vm.AvailableLogs);
        Assert.Contains("Security", vm.AvailableLogs);
        Assert.Contains("Setup", vm.AvailableLogs);
    }

    [Fact]
    public void CopySelected_WithNull_DoesNotThrow()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        vm.SelectedEntry = null;
        var ex = Record.Exception(() => vm.CopySelectedCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void Counts_StartAtZero()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        Assert.Equal(0, vm.CriticalCount);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.Equal(0, vm.InfoCount);
    }

    // ── Refresh re-entrancy gate (regression: double-invoke pollutes Entries) ──

    [Fact]
    public void RefreshCommand_DisabledWhileBusy()
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        Assert.True(vm.RefreshCommand.CanExecute(null));   // idle → allowed

        vm.IsBusy = true;
        Assert.False(vm.RefreshCommand.CanExecute(null));  // scanning → blocked (no second run)

        vm.IsBusy = false;
        Assert.True(vm.RefreshCommand.CanExecute(null));   // done → allowed again
    }

    // ── Row marking ──────────────────────────────────────────────────────────────────────────────
    //
    // ToggleHighlightCommand and FriendlyEventEntry.IsHighlighted shipped with the "row highlight"
    // feature commit, which touched two models, two view models and the CHANGELOG — and no view. The
    // announced ability to "toggle highlight on any log entry" therefore had no control to invoke it,
    // and nothing rendered the mark. RowMarkBindingTests covers the binding half; these cover the
    // behaviour the UI now relies on.

    private static LogsViewModel WithEntries(params FriendlyEventEntry[] entries)
    {
        var vm = new LogsViewModel(new Services.EventLogService());
        foreach (var e in entries) vm.Entries.Add(e);
        return vm;
    }

    [Fact]
    public void ToggleHighlight_MarksTheEntry_AndCountsIt()
    {
        var target = Make(EventSeverity.Error, "disk controller reset");
        var vm = WithEntries(Make(EventSeverity.Warning, "other"), target);

        Assert.False(target.IsHighlighted);
        Assert.Equal(0, vm.HighlightedCount);

        vm.ToggleHighlightCommand.Execute(target);

        Assert.True(target.IsHighlighted);
        Assert.Equal(1, vm.HighlightedCount);
    }

    [Fact]
    public void ToggleHighlight_Twice_UnmarksTheEntry()
    {
        var target = Make(EventSeverity.Error, "disk controller reset");
        var vm = WithEntries(target);

        vm.ToggleHighlightCommand.Execute(target);
        vm.ToggleHighlightCommand.Execute(target);

        Assert.False(target.IsHighlighted);
        Assert.Equal(0, vm.HighlightedCount);
    }

    [Fact]
    public void ToggleHighlight_IgnoresAnythingThatIsNotAnEventRow()
    {
        // The command takes object? because the row arrives as CommandParameter. A stray parameter has
        // to be a no-op rather than a cast exception on the UI thread.
        var vm = WithEntries(Make(EventSeverity.Error, "x"));

        Assert.Null(Record.Exception(() => vm.ToggleHighlightCommand.Execute(null)));
        Assert.Null(Record.Exception(() => vm.ToggleHighlightCommand.Execute(42)));
        Assert.Equal(0, vm.HighlightedCount);
    }

    [Fact]
    public void AMarkedEntry_StaysMarkedWhenTheSeverityFilterHidesIt()
    {
        // The whole point: mark an event, carry on filtering, still find it afterwards. Filtering runs
        // through EntriesView — an ICollectionView over the same instances — so the mark rides along.
        var info = Make(EventSeverity.Info, "informational note");
        var vm = WithEntries(info);
        vm.ShowInfo = true;

        vm.ToggleHighlightCommand.Execute(info);
        Assert.True(InvokeFilter(vm, info));    // visible and marked

        vm.ShowInfo = false;                    // filtered out of sight
        Assert.False(InvokeFilter(vm, info));
        Assert.True(info.IsHighlighted);        // still marked
        Assert.Equal(1, vm.HighlightedCount);   // and still counted

        vm.ShowInfo = true;
        Assert.True(InvokeFilter(vm, info));
        Assert.True(info.IsHighlighted);
    }

    [Fact]
    public void ClearHighlights_ClearsMarksOnEntriesTheFilterIsHiding()
    {
        // The negative case worth pinning. Clearing only what the view currently shows would leave
        // marks on hidden rows, so "Clear 2 marks" would clear one and the button would remain,
        // claiming a mark the user cannot see or reach.
        var visible = Make(EventSeverity.Error, "visible");
        var hidden = Make(EventSeverity.Info, "hidden by the severity filter");
        var vm = WithEntries(visible, hidden);

        vm.ToggleHighlightCommand.Execute(visible);
        vm.ToggleHighlightCommand.Execute(hidden);
        Assert.Equal(2, vm.HighlightedCount);

        Assert.False(InvokeFilter(vm, hidden));   // Info is off by default — genuinely hidden

        vm.ClearHighlightsCommand.Execute(null);

        Assert.False(visible.IsHighlighted);
        Assert.False(hidden.IsHighlighted);
        Assert.Equal(0, vm.HighlightedCount);
    }

    [Fact]
    public void LoadingADifferentLog_DropsTheMarkCount()
    {
        // Entries are rebuilt per load, so the marked objects are gone; a stale count would keep the
        // "Clear N marks" button on screen offering to clear marks that no longer exist.
        var entry = Make(EventSeverity.Error, "from the previous log");
        var vm = WithEntries(entry);
        vm.ToggleHighlightCommand.Execute(entry);
        Assert.Equal(1, vm.HighlightedCount);

        // What RefreshAsync does before querying: clear, then reset the counters.
        vm.Entries.Clear();
        typeof(LogsViewModel)
            .GetMethod("ResetCounts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

        Assert.Equal(0, vm.HighlightedCount);
    }
    // ---------- the status sentence ----------

    [Fact]
    public void DescribeLoad_PartialReadAfterAFault_SaysTheListIsIncomplete()
    {
        // A read that ends early because the reader faulted keeps the records it already emitted, and the
        // count-only wording announced those as a finished load — "Loaded 137 events from System" about a
        // list that stopped halfway. Same mistake as calling a cancelled scan complete.
        var message = LogsViewModel.DescribeLoad(137, EventLogService.ReadOutcome.Unavailable, "System");

        Assert.Contains("137", message);
        Assert.Contains("incomplete", message);
        Assert.DoesNotContain("could not be opened", message);
    }

    [Theory]
    [InlineData(EventLogService.ReadOutcome.AccessDenied, "administrator rights")]
    [InlineData(EventLogService.ReadOutcome.LogNotFound, "does not exist")]
    [InlineData(EventLogService.ReadOutcome.Unavailable, "could not be opened")]
    public void DescribeLoad_EmptyResult_ExplainsWhyRatherThanSayingZero(
        EventLogService.ReadOutcome outcome, string expected)
    {
        // "Loaded 0 events" followed by "No events match your filters" told a standard user selecting
        // Security that their filters matched nothing, when in fact the log was never read.
        var message = LogsViewModel.DescribeLoad(0, outcome, "Security");

        Assert.Contains(expected, message);
        Assert.DoesNotContain("Loaded 0", message);
    }

    [Fact]
    public void DescribeLoad_CleanRead_StaysShort()
    {
        // The negative case: a healthy load must not grow a caveat.
        var message = LogsViewModel.DescribeLoad(42, EventLogService.ReadOutcome.Ok, "Application");

        Assert.Equal("Loaded 42 events from Application", message);
    }

    // ---------- the status line counts every event the load added (#2480) ----------

    /// <summary>
    /// The load runs under a context that behaves like WPF's: posted work waits in a queue until it is pumped, and
    /// each piece of work runs under a NEW context instance, as WPF installs a fresh
    /// <c>DispatcherSynchronizationContext</c> for every dispatcher operation.
    /// </summary>
    /// <remarks>
    /// Without a context, which is how every other test here builds the view model, the batches were added inline
    /// and the count was right, so no test could see the defect. It needs the real mechanism: the old helper
    /// compared <see cref="SynchronizationContext.Current"/> with the instance captured at construction, found them
    /// different after the first <c>await</c>, and queued every batch behind the status line.
    /// </remarks>
    [Theory]
    [InlineData(7)]     // fewer than one batch of 50: the status line said "Loaded 0 events"
    [InlineData(120)]   // two whole batches and a partial one: it said 100
    public void Refresh_CountsEveryEventItLoaded(int loaded)
    {
        var queue = new Queue<(SendOrPostCallback Work, object? State)>();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherLikeContext(queue));
        try
        {
            var vm = new LogsViewModel(new EventLogService(), (_, ct) => EventsAsync(loaded, ct));

            var refresh = vm.RefreshCommand.ExecuteAsync(null);
            PumpUntilDone(queue, refresh);

            Assert.Equal(loaded, vm.Entries.Count);
            Assert.Equal($"Loaded {loaded} events from {vm.SelectedLog}", vm.StatusMessage);
            Assert.False(vm.LoadWasRefused);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Events that arrive one continuation at a time, as the real reader's do.</summary>
    private static async IAsyncEnumerable<FriendlyEventEntry> EventsAsync(
        int count, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return Make(EventSeverity.Error, $"event {i}", id: i);
        }
    }

    /// <summary>
    /// Runs the queued work, each item under a fresh context, until <paramref name="done"/> has completed and
    /// nothing is left queued. Single-threaded, so the order is the order WPF would run it in.
    /// </summary>
    private static void PumpUntilDone(Queue<(SendOrPostCallback Work, object? State)> queue, Task done)
    {
        var ran = 0;
        while (!done.IsCompleted || queue.Count > 0)
        {
            Assert.True(queue.Count > 0, "the load is waiting on something that is not queued here");
            Assert.True(++ran < 100_000, "the load never finished");
            var (work, state) = queue.Dequeue();
            SynchronizationContext.SetSynchronizationContext(new DispatcherLikeContext(queue));
            work(state);
        }
        done.GetAwaiter().GetResult();   // surfaces a fault instead of passing over it
    }

    private sealed class DispatcherLikeContext(Queue<(SendOrPostCallback Work, object? State)> queue)
        : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    // ---------- switching on a severity the list was not loaded with loads it (#2601) ----------
    //
    // The switches are part of the query and also a filter over what it listed. Info and Verbose start off, so
    // switching either on after a load filtered a list that held none of them, and nothing appeared until Refresh.

    /// <summary>
    /// A log read the way Windows reads one: only the severities asked for, as new rows on every read. Records what
    /// each read asked for, and can hold a read at the event it would list at <see cref="PauseAt"/>.
    /// </summary>
    private sealed class FakeLog
    {
        private static readonly DateTime FirstEvent = new(2026, 10, 9, 8, 0, 0);

        public List<(EventSeverity Severity, long Record, int Minute)> Events { get; set; } = [];

        public List<List<EventSeverity>> Asked { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public int PauseAt { get; set; }

        public async IAsyncEnumerable<FriendlyEventEntry> ReadAsync(
            EventLogQueryOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Asked.Add([.. options.Severities ?? []]);
            var listed = 0;
            foreach (var (severity, record, minute) in Events)
            {
                if (options.Severities?.Contains(severity) == false) continue;
                if (listed++ == PauseAt && Gate is { } gate) await gate.Task.WaitAsync(ct);
                yield return new FriendlyEventEntry
                {
                    LogName = options.LogName,
                    RecordId = record,
                    Timestamp = FirstEvent.AddMinutes(minute),
                    Severity = severity,
                    Message = $"{severity} {record}",
                    // Filled in, so opening a row does not read this PC's event log for it.
                    Xml = "<Event/>",
                };
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="test"/> under the dispatcher-like context, so every load runs on this thread, as it does
    /// on the UI thread, and the test pumps it to its end.
    /// </summary>
    private static void OnADispatcher(Action<Queue<(SendOrPostCallback Work, object? State)>> test)
    {
        var queue = new Queue<(SendOrPostCallback Work, object? State)>();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherLikeContext(queue));
        try
        {
            test(queue);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void SwitchingOnASeverityTheLoadDidNotAskFor_LoadsTheListAgainWithIt()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Info, 2, 2), (EventSeverity.Info, 3, 3)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            Assert.Equal(1, vm.VisibleCount);

            vm.ShowInfo = true;
            PumpUntilDone(queue, vm.RefreshCommand.ExecutionTask!);

            Assert.Equal(2, log.Asked.Count);
            Assert.Contains(EventSeverity.Info, log.Asked[1]);
            Assert.Equal(3, vm.VisibleCount);
            Assert.Equal("Loaded 3 events from System", vm.StatusMessage);
        });
    }

    [Fact]
    public void SwitchingASeverityOff_OnlyFilters_AndOnAgain_DoesNotLoadAgain()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Info, 2, 2)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync) { ShowInfo = true };
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));

            vm.ShowInfo = false;
            Assert.Equal(1, vm.VisibleCount);
            vm.ShowInfo = true;
            Assert.Equal(2, vm.VisibleCount);

            Assert.Single(log.Asked);
        });
    }

    [Fact]
    public void BeforeAnyLoad_SwitchingASeverityOn_LoadsNothing()
    {
        // The first load asks for whatever is switched on by then.
        var log = new FakeLog { Events = [(EventSeverity.Info, 1, 1)] };
        var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);

        vm.ShowInfo = true;
        vm.ShowVerbose = true;

        Assert.Empty(log.Asked);
    }

    [Fact]
    public void ASeveritySwitchedOnWhileALoadRuns_IsLoadedWhenThatLoadEnds()
    {
        var gate = new TaskCompletionSource();
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Info, 2, 2)], Gate = gate };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            var refresh = vm.RefreshCommand.ExecuteAsync(null);

            vm.ShowInfo = true;
            Assert.Single(log.Asked);   // no second load beside the one running
            gate.SetResult();
            PumpUntilDone(queue, refresh);

            Assert.Equal(2, log.Asked.Count);
            Assert.Contains(EventSeverity.Info, log.Asked[1]);
            Assert.Equal(2, vm.Entries.Count);
        });
    }

    [Fact]
    public void ACancelledLoad_IsNotFollowedByAnother()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Info, 2, 2)], Gate = new TaskCompletionSource() };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            var refresh = vm.RefreshCommand.ExecuteAsync(null);

            vm.ShowInfo = true;
            vm.CancelCommand.Execute(null);
            PumpUntilDone(queue, refresh);

            Assert.Single(log.Asked);
            Assert.Equal("Cancelled", vm.StatusMessage);
        });
    }

    // A load builds new rows, so loading again on a switch the user took for a filter would have dropped every mark
    // and closed the event they were reading. An event listed again keeps both.

    [Fact]
    public void ALoad_GivesAnEventItListsAgainBackItsMark()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Error, 2, 2), (EventSeverity.Info, 3, 3)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.ToggleHighlightCommand.Execute(vm.Entries.Single(e => e.RecordId == 1));

            vm.ShowInfo = true;
            PumpUntilDone(queue, vm.RefreshCommand.ExecutionTask!);

            Assert.Equal(3, vm.Entries.Count);
            var marked = Assert.Single(vm.Entries, e => e.IsHighlighted);
            Assert.Equal(1, marked.RecordId);
            Assert.Equal(1, vm.HighlightedCount);
        });
    }

    [Fact]
    public void AnEventInAnotherLog_WithTheSameRecordId_IsNotMarked()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.ToggleHighlightCommand.Execute(vm.Entries.Single());

            vm.SelectedLog = "Application";
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));

            Assert.False(vm.Entries.Single().IsHighlighted);
            Assert.Equal(0, vm.HighlightedCount);
        });
    }

    [Fact]
    public void ARecordIdHandedOutAgain_AfterTheLogWasCleared_IsNotMarked()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.ToggleHighlightCommand.Execute(vm.Entries.Single());

            log.Events = [(EventSeverity.Error, 1, 30)];   // record 1 is now a later event
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));

            Assert.False(vm.Entries.Single().IsHighlighted);
            Assert.Equal(0, vm.HighlightedCount);
        });
    }

    [Fact]
    public void ALoad_KeepsTheOpenEventOpen_WhenItListsItAgain()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1), (EventSeverity.Error, 2, 2), (EventSeverity.Info, 3, 3)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.SelectedEntry = vm.Entries.Single(e => e.RecordId == 2);

            vm.ShowInfo = true;
            PumpUntilDone(queue, vm.RefreshCommand.ExecutionTask!);

            Assert.NotNull(vm.SelectedEntry);
            Assert.Equal(2, vm.SelectedEntry.RecordId);
            Assert.Contains(vm.SelectedEntry, vm.Entries);
        });
    }

    [Fact]
    public void ALoad_ThatDoesNotListTheOpenEventAgain_LeavesNothingOpen()
    {
        var log = new FakeLog { Events = [(EventSeverity.Error, 1, 1)] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.SelectedEntry = vm.Entries.Single();

            vm.SelectedLog = "Application";
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));

            Assert.Null(vm.SelectedEntry);
        });
    }

    [Fact]
    public void AnEventOpenedWhileALoadRuns_StaysOpen_RatherThanTheOneOpenBefore()
    {
        var log = new FakeLog { Events = [.. Enumerable.Range(1, 60).Select(i => (EventSeverity.Error, (long)i, i))] };
        OnADispatcher(queue =>
        {
            var vm = new LogsViewModel(new EventLogService(), log.ReadAsync);
            PumpUntilDone(queue, vm.RefreshCommand.ExecuteAsync(null));
            vm.SelectedEntry = vm.Entries.Single(e => e.RecordId == 60);

            var gate = new TaskCompletionSource();
            log.Gate = gate;
            log.PauseAt = 55;   // by then the first 50 are listed, as one batch
            var refresh = vm.RefreshCommand.ExecuteAsync(null);
            vm.SelectedEntry = vm.Entries.Single(e => e.RecordId == 10);
            gate.SetResult();
            PumpUntilDone(queue, refresh);

            Assert.Equal(10, vm.SelectedEntry?.RecordId);
            Assert.Contains(vm.SelectedEntry, vm.Entries);
        });
    }
}
