// SysManager · LogsViewModelExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Extended pure unit tests for <see cref="LogsViewModel"/>.
/// Complements the existing LogsViewModelTests with coverage for helpers,
/// edge cases, and property defaults not yet exercised.
/// </summary>
public class LogsViewModelExtendedTests
{
    // ---------- reflection helpers ----------

    private static readonly MethodInfo _entryFilter =
        typeof(LogsViewModel).GetMethod("EntryFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo _buildSeverityFilter =
        typeof(LogsViewModel).GetMethod("BuildSeverityFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo _resolveSince =
        typeof(LogsViewModel).GetMethod("ResolveSince", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo _csv =
        typeof(LogsViewModel).GetMethod("Csv", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo _containsCi =
        typeof(LogsViewModel).GetMethod("ContainsCi", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool Filter(LogsViewModel vm, FriendlyEventEntry e) =>
        (bool)_entryFilter.Invoke(vm, new object[] { e })!;

    private static List<EventSeverity> BuildSev(LogsViewModel vm) =>
        (List<EventSeverity>)_buildSeverityFilter.Invoke(vm, null)!;

    private static DateTime? ResolveSince(string range) =>
        (DateTime?)_resolveSince.Invoke(null, new object[] { range });

    private static string Csv(string? s) =>
        (string)_csv.Invoke(null, new object?[] { s })!;

    private static bool ContainsCi(string? s, string q) =>
        (bool)_containsCi.Invoke(null, new object?[] { s, q })!;

    private static FriendlyEventEntry Make(EventSeverity sev, string msg = "", string provider = "X", int id = 1)
        => new()
        {
            Severity = sev,
            Message = msg,
            FullMessage = msg,
            ProviderName = provider,
            EventId = id
        };

    // ---------- property defaults ----------

    [Fact]
    public void SelectedLog_DefaultsToSystem()
    {
        var vm = new LogsViewModel();
        Assert.Equal("System", vm.SelectedLog);
    }

    [Fact]
    public void SelectedMaxResults_DefaultsTo500()
    {
        var vm = new LogsViewModel();
        Assert.Equal("500", vm.SelectedMaxResults);
    }

    [Fact]
    public void SearchText_DefaultsEmpty()
    {
        var vm = new LogsViewModel();
        Assert.Equal("", vm.SearchText);
    }

    [Fact]
    public void SelectedEntry_DefaultsNull()
    {
        var vm = new LogsViewModel();
        Assert.Null(vm.SelectedEntry);
    }

    [Fact]
    public void MaxResultOptions_ContainsExpectedValues()
    {
        var vm = new LogsViewModel();
        Assert.Contains("200", vm.MaxResultOptions);
        Assert.Contains("500", vm.MaxResultOptions);
        Assert.Contains("1000", vm.MaxResultOptions);
        Assert.Contains("5000", vm.MaxResultOptions);
    }

    [Fact]
    public void LogFolder_IsNonEmpty()
    {
        var vm = new LogsViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.LogFolder));
    }

    [Fact]
    public void Entries_StartsEmpty()
    {
        var vm = new LogsViewModel();
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void EntriesView_IsNotNull()
    {
        var vm = new LogsViewModel();
        Assert.NotNull(vm.EntriesView);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("RefreshCommand")]
    [InlineData("CancelCommand")]
    [InlineData("OpenLogFolderCommand")]
    [InlineData("OpenEventViewerCommand")]
    [InlineData("CopySelectedCommand")]
    [InlineData("ExportCsvCommand")]
    [InlineData("SearchOnlineCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = new LogsViewModel();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- Cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = new LogsViewModel();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    // ---------- BuildSeverityFilter ----------

    [Fact]
    public void BuildSeverityFilter_DefaultIncludesCriticalErrorWarning()
    {
        var vm = new LogsViewModel();
        var list = BuildSev(vm);
        Assert.Contains(EventSeverity.Critical, list);
        Assert.Contains(EventSeverity.Error, list);
        Assert.Contains(EventSeverity.Warning, list);
        Assert.DoesNotContain(EventSeverity.Info, list);
        Assert.DoesNotContain(EventSeverity.Verbose, list);
    }

    [Fact]
    public void BuildSeverityFilter_AllOn_ReturnsFive()
    {
        var vm = new LogsViewModel
        {
            ShowCritical = true, ShowError = true, ShowWarning = true,
            ShowInfo = true, ShowVerbose = true
        };
        Assert.Equal(5, BuildSev(vm).Count);
    }

    [Fact]
    public void BuildSeverityFilter_AllOff_ReturnsEmpty()
    {
        var vm = new LogsViewModel
        {
            ShowCritical = false, ShowError = false, ShowWarning = false,
            ShowInfo = false, ShowVerbose = false
        };
        Assert.Empty(BuildSev(vm));
    }

    // ---------- ResolveSince ----------

    [Theory]
    [InlineData("Last hour")]
    [InlineData("Last 24 hours")]
    [InlineData("Last 7 days")]
    [InlineData("Last 30 days")]
    public void ResolveSince_KnownRanges_ReturnsPastDate(string range)
    {
        var result = ResolveSince(range);
        Assert.NotNull(result);
        Assert.True(result!.Value < DateTime.Now);
    }

    [Fact]
    public void ResolveSince_All_ReturnsNull()
    {
        Assert.Null(ResolveSince("All"));
    }

    [Fact]
    public void ResolveSince_Unknown_FallsBackTo24Hours()
    {
        var result = ResolveSince("garbage");
        Assert.NotNull(result);
        // Should be roughly 24h ago (within a few seconds tolerance)
        var diff = DateTime.Now - result!.Value;
        Assert.InRange(diff.TotalHours, 23.9, 24.1);
    }

    // ---------- Csv helper ----------

    [Fact]
    public void Csv_PlainString_ReturnedAsIs()
    {
        Assert.Equal("hello", Csv("hello"));
    }

    [Fact]
    public void Csv_Null_ReturnsEmpty()
    {
        Assert.Equal("", Csv(null));
    }

    [Fact]
    public void Csv_ContainsComma_Quoted()
    {
        var result = Csv("a,b");
        Assert.StartsWith("\"", result);
        Assert.EndsWith("\"", result);
        Assert.Contains("a,b", result);
    }

    [Fact]
    public void Csv_ContainsQuote_DoubleEscaped()
    {
        var result = Csv("say \"hi\"");
        Assert.Contains("\"\"hi\"\"", result);
    }

    [Fact]
    public void Csv_ContainsNewline_Quoted()
    {
        Assert.StartsWith("\"", Csv("line1\nline2"));
    }

    [Fact]
    public void Csv_ContainsCarriageReturn_Quoted()
    {
        Assert.StartsWith("\"", Csv("line1\rline2"));
    }

    // ---------- ContainsCi ----------

    [Fact]
    public void ContainsCi_CaseInsensitiveMatch()
    {
        Assert.True(ContainsCi("Hello World", "hello"));
        Assert.True(ContainsCi("Hello World", "WORLD"));
    }

    [Fact]
    public void ContainsCi_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(ContainsCi(null, "x"));
        Assert.False(ContainsCi("", "x"));
    }

    [Fact]
    public void ContainsCi_NoMatch_ReturnsFalse()
    {
        Assert.False(ContainsCi("abc", "xyz"));
    }

    // ---------- filter edge cases ----------

    [Fact]
    public void Filter_NonFriendlyEventEntry_ReturnsFalse()
    {
        var vm = new LogsViewModel();
        var result = (bool)_entryFilter.Invoke(vm, new object[] { "not an entry" })!;
        Assert.False(result);
    }

    [Fact]
    public void Filter_AllSeveritiesOff_RejectsEverything()
    {
        var vm = new LogsViewModel
        {
            ShowCritical = false, ShowError = false, ShowWarning = false,
            ShowInfo = false, ShowVerbose = false
        };
        Assert.False(Filter(vm, Make(EventSeverity.Critical)));
        Assert.False(Filter(vm, Make(EventSeverity.Error)));
        Assert.False(Filter(vm, Make(EventSeverity.Warning)));
        Assert.False(Filter(vm, Make(EventSeverity.Info)));
        Assert.False(Filter(vm, Make(EventSeverity.Verbose)));
    }

    [Fact]
    public void Filter_SearchMatchesFullMessage()
    {
        var vm = new LogsViewModel();
        var e = new FriendlyEventEntry
        {
            Severity = EventSeverity.Error,
            Message = "short",
            FullMessage = "this is the full detailed message with keyword",
            ProviderName = "P",
            EventId = 1
        };
        vm.SearchText = "keyword";
        Assert.True(Filter(vm, e));
    }

    // ---------- setters fire PropertyChanged ----------

    [Theory]
    [InlineData(nameof(LogsViewModel.ShowCritical))]
    [InlineData(nameof(LogsViewModel.ShowError))]
    [InlineData(nameof(LogsViewModel.ShowWarning))]
    [InlineData(nameof(LogsViewModel.ShowInfo))]
    [InlineData(nameof(LogsViewModel.ShowVerbose))]
    [InlineData(nameof(LogsViewModel.SearchText))]
    [InlineData(nameof(LogsViewModel.SelectedLog))]
    [InlineData(nameof(LogsViewModel.SelectedTimeRange))]
    [InlineData(nameof(LogsViewModel.SelectedMaxResults))]
    public void Setter_FiresPropertyChanged(string propName)
    {
        var vm = new LogsViewModel();
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == propName) fired = true; };

        var prop = typeof(LogsViewModel).GetProperty(propName)!;
        var current = prop.GetValue(vm);
        // Flip to a different value
        if (current is bool b) prop.SetValue(vm, !b);
        else if (current is string) prop.SetValue(vm, "changed_" + Guid.NewGuid().ToString("N")[..6]);

        Assert.True(fired, $"PropertyChanged not fired for {propName}");
    }
}
