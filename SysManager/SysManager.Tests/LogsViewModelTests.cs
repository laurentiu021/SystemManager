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
}
