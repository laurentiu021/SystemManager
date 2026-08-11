// SysManager · LogsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
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
}
