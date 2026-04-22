// SysManager · LogsViewModelExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Exhaustive coverage for LogsViewModel behavior — filters, search,
/// counts, copy, export. Network / Event Log integration is covered
/// separately in EventLogServiceTests.
/// </summary>
public class LogsViewModelExtendedTests
{
    private static bool InvokeFilter(LogsViewModel vm, FriendlyEventEntry e)
    {
        var m = typeof(LogsViewModel).GetMethod("EntryFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)m.Invoke(vm, new object[] { e })!;
    }

    private static void InvokeUpdateCounts(LogsViewModel vm, FriendlyEventEntry e, int delta)
    {
        var m = typeof(LogsViewModel).GetMethod("UpdateCounts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object?[] { e, delta });
    }

    private static FriendlyEventEntry Make(EventSeverity sev, string msg = "", string provider = "X", int id = 1, string? full = null)
        => new()
        {
            Severity = sev,
            Message = msg,
            FullMessage = full ?? msg,
            ProviderName = provider,
            EventId = id,
            SeverityLabel = sev.ToString()
        };

    // ---------- defaults ----------

    [Fact]
    public void Defaults_SelectedLogIsSystem()
    {
        var vm = new LogsViewModel();
        Assert.Equal("System", vm.SelectedLog);
    }

    [Fact]
    public void Defaults_MaxResultsIs500()
    {
        var vm = new LogsViewModel();
        Assert.Equal("500", vm.SelectedMaxResults);
    }

    [Fact]
    public void Defaults_LogFolderIsSet()
    {
        var vm = new LogsViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.LogFolder));
    }

    [Fact]
    public void Defaults_SearchTextIsEmpty()
    {
        var vm = new LogsViewModel();
        Assert.Equal("", vm.SearchText);
    }

    [Fact]
    public void Defaults_SelectedEntryIsNull()
    {
        var vm = new LogsViewModel();
        Assert.Null(vm.SelectedEntry);
    }

    [Fact]
    public void Defaults_CollectionViewIsSet()
    {
        var vm = new LogsViewModel();
        Assert.NotNull(vm.EntriesView);
    }

    // ---------- MaxResultOptions ----------

    [Fact]
    public void MaxResultOptions_IsDescendingSafe()
    {
        var vm = new LogsViewModel();
        foreach (var opt in vm.MaxResultOptions)
            Assert.True(int.TryParse(opt, out var v) && v > 0);
    }

    // ---------- Filter – severity matrix ----------

    [Theory]
    [InlineData(EventSeverity.Critical, "ShowCritical")]
    [InlineData(EventSeverity.Error, "ShowError")]
    [InlineData(EventSeverity.Warning, "ShowWarning")]
    [InlineData(EventSeverity.Info, "ShowInfo")]
    [InlineData(EventSeverity.Verbose, "ShowVerbose")]
    public void Filter_DisabledSeverity_Hides(EventSeverity sev, string propertyName)
    {
        var vm = new LogsViewModel();
        typeof(LogsViewModel).GetProperty(propertyName)!.SetValue(vm, true);
        Assert.True(InvokeFilter(vm, Make(sev)));
        typeof(LogsViewModel).GetProperty(propertyName)!.SetValue(vm, false);
        Assert.False(InvokeFilter(vm, Make(sev)));
    }

    [Fact]
    public void Filter_SearchText_IsCaseInsensitive()
    {
        var vm = new LogsViewModel();
        var e = Make(EventSeverity.Error, "Disk controller timeout");
        vm.SearchText = "DISK";
        Assert.True(InvokeFilter(vm, e));
        vm.SearchText = "disk";
        Assert.True(InvokeFilter(vm, e));
        vm.SearchText = "Disk";
        Assert.True(InvokeFilter(vm, e));
    }

    [Fact]
    public void Filter_SearchByProvider_Works()
    {
        var vm = new LogsViewModel();
        var e = Make(EventSeverity.Error, "m", "Microsoft-Windows-Kernel-Power", 41);
        vm.SearchText = "Kernel";
        Assert.True(InvokeFilter(vm, e));
    }

    [Fact]
    public void Filter_SearchByEventIdNumeric_Works()
    {
        var vm = new LogsViewModel();
        var e = Make(EventSeverity.Error, "m", "x", 41);
        vm.SearchText = "41";
        Assert.True(InvokeFilter(vm, e));
    }

    [Fact]
    public void Filter_SearchMissingText_ReturnsFalse()
    {
        var vm = new LogsViewModel();
        var e = Make(EventSeverity.Error, "simple message");
        vm.SearchText = "nowhere-to-be-found";
        Assert.False(InvokeFilter(vm, e));
    }

    [Fact]
    public void Filter_OnNonEntry_ReturnsFalse()
    {
        var vm = new LogsViewModel();
        var m = typeof(LogsViewModel).GetMethod("EntryFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (bool)m.Invoke(vm, new object[] { new object() })!;
        Assert.False(result);
    }

    // ---------- Counts ----------

    [Fact]
    public void UpdateCounts_IncrementsCorrectCounter()
    {
        var vm = new LogsViewModel();
        InvokeUpdateCounts(vm, Make(EventSeverity.Critical), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Error), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Error), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Warning), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Info), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Info), 1);
        InvokeUpdateCounts(vm, Make(EventSeverity.Info), 1);
        Assert.Equal(1, vm.CriticalCount);
        Assert.Equal(2, vm.ErrorCount);
        Assert.Equal(1, vm.WarningCount);
        Assert.Equal(3, vm.InfoCount);
    }

    [Fact]
    public void UpdateCounts_Decrement_SubtractsCorrectly()
    {
        var vm = new LogsViewModel();
        InvokeUpdateCounts(vm, Make(EventSeverity.Error), 5);
        InvokeUpdateCounts(vm, Make(EventSeverity.Error), -2);
        Assert.Equal(3, vm.ErrorCount);
    }

    [Fact]
    public void UpdateCounts_Verbose_DoesNothing()
    {
        var vm = new LogsViewModel();
        InvokeUpdateCounts(vm, Make(EventSeverity.Verbose), 10);
        Assert.Equal(0, vm.CriticalCount);
        Assert.Equal(0, vm.ErrorCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.Equal(0, vm.InfoCount);
    }

    // ---------- Commands that don't depend on WinAPI ----------

    [Fact]
    public void CancelCommand_WithoutActiveJob_IsSafe()
    {
        var vm = new LogsViewModel();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CopySelected_WithSelectedEntry_DoesNotThrow()
    {
        var vm = new LogsViewModel();
        vm.SelectedEntry = Make(EventSeverity.Error, "oops", "Test", 1000, full: "full message");
        vm.SelectedEntry.Explanation = "it broke";
        vm.SelectedEntry.Recommendation = "restart";
        var ex = Record.Exception(() => vm.CopySelectedCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void SearchOnline_WithoutSelection_IsSafe()
    {
        var vm = new LogsViewModel();
        vm.SelectedEntry = null;
        var ex = Record.Exception(() => vm.SearchOnlineCommand.Execute(null));
        Assert.Null(ex);
    }

    // ---------- Preference changes refresh view ----------

    [Fact]
    public void ChangingShowCritical_DoesNotThrow()
    {
        var vm = new LogsViewModel();
        vm.ShowCritical = false;
        vm.ShowCritical = true;
    }

    [Fact]
    public void ChangingSearchText_RefreshesView_NoThrow()
    {
        var vm = new LogsViewModel();
        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 20; i++) vm.SearchText = $"query{i}";
        });
        Assert.Null(ex);
    }

    // ---------- Time range ----------

    [Theory]
    [InlineData("Last hour")]
    [InlineData("Last 24 hours")]
    [InlineData("Last 7 days")]
    [InlineData("Last 30 days")]
    [InlineData("All")]
    public void SelectedTimeRange_AcceptsAllOptions(string range)
    {
        var vm = new LogsViewModel();
        vm.SelectedTimeRange = range;
        Assert.Equal(range, vm.SelectedTimeRange);
    }

    // ---------- AvailableLogs ----------

    [Fact]
    public void AvailableLogs_HasWindowsCoreLogs()
    {
        var vm = new LogsViewModel();
        Assert.Contains("System", vm.AvailableLogs);
        Assert.Contains("Application", vm.AvailableLogs);
        Assert.Contains("Security", vm.AvailableLogs);
        Assert.Contains("Setup", vm.AvailableLogs);
    }

    // ---------- Refresh command smoke ----------

    [Fact]
    public async Task RefreshCommand_CompletesOnInvalidLog_Gracefully()
    {
        var vm = new LogsViewModel();
        vm.SelectedLog = "Bogus-Log-XYZ";
        vm.SelectedMaxResults = "50";
        await vm.RefreshCommand.ExecuteAsync(null);
        // Must not throw; entries empty; status message set.
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }
}
