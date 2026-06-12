// SysManager · LogsViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Friendly Windows Event Log browser. Loads entries asynchronously, filters
/// live via a CollectionView, and shows a plain-English explanation for the
/// selected entry. Supports export to CSV and jumping to the raw log file.
/// </summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    private readonly EventLogService _eventLogs;
    private readonly SynchronizationContext? _sync;
    private CancellationTokenSource? _cts;

    // Characters that force a CSV field to be quoted. Hoisted to a SearchValues so the
    // per-field scan in Csv() doesn't allocate a char[] on every call during export.
    private static readonly SearchValues<char> CsvSpecials = SearchValues.Create(",\"\n\r");

    public BulkObservableCollection<FriendlyEventEntry> Entries { get; } = new();
    public ICollectionView EntriesView { get; }

    public string[] AvailableLogs { get; } = { "System", "Application", "Security", "Setup" };
    public string[] TimeRanges { get; } = { "Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "All" };
    public string[] MaxResultOptions { get; } = { "200", "500", "1000", "5000" };

    [ObservableProperty] private string _selectedLog = "System";
    [ObservableProperty] private string _selectedTimeRange = "Last 24 hours";
    [ObservableProperty] private string _selectedMaxResults = "500";

    [ObservableProperty] private bool _showCritical = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showInfo;
    [ObservableProperty] private bool _showVerbose;

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private FriendlyEventEntry? _selectedEntry;

    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;

    [ObservableProperty] private string _logFolder = LogService.LogDir;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private bool _hasNoResults;

    public LogsViewModel(EventLogService eventLogs)
    {
        _eventLogs = eventLogs;
        _sync = SynchronizationContext.Current;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = EntryFilter;
    }

    private void UpdateVisibleCount()
    {
        // PERF-002: Use CollectionView.Count directly instead of iterating
        // the entire filtered collection via Cast<object>().Count().
        VisibleCount = (EntriesView as CollectionView)?.Count ?? EntriesView.Cast<object>().Count();
        HasNoResults = Entries.Count > 0 && VisibleCount == 0;
    }

    // ---------- Filter changes refresh the view ----------

    partial void OnShowCriticalChanged(bool value) { EntriesView.Refresh(); UpdateVisibleCount(); }
    partial void OnShowErrorChanged(bool value) { EntriesView.Refresh(); UpdateVisibleCount(); }
    partial void OnShowWarningChanged(bool value) { EntriesView.Refresh(); UpdateVisibleCount(); }
    partial void OnShowInfoChanged(bool value) { EntriesView.Refresh(); UpdateVisibleCount(); }
    partial void OnShowVerboseChanged(bool value) { EntriesView.Refresh(); UpdateVisibleCount(); }
    partial void OnFilterTextChanged(string value) { EntriesView.Refresh(); UpdateVisibleCount(); }

    private bool EntryFilter(object o)
    {
        if (o is not FriendlyEventEntry e) return false;

        var sevOk = e.Severity switch
        {
            EventSeverity.Critical => ShowCritical,
            EventSeverity.Error => ShowError,
            EventSeverity.Warning => ShowWarning,
            EventSeverity.Info => ShowInfo,
            EventSeverity.Verbose => ShowVerbose,
            _ => true
        };
        if (!sevOk) return false;

        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        var q = FilterText.Trim();
        return ContainsCi(e.Message, q)
            || ContainsCi(e.ProviderName, q)
            || ContainsCi(e.FullMessage, q)
            || e.EventId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCi(string? s, string q)
        => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    // ---------- Commands ----------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading events…";
        Entries.Clear();
        ResetCounts();

        var opt = new EventLogQueryOptions
        {
            LogName = SelectedLog,
            Since = ResolveSince(SelectedTimeRange),
            MaxResults = int.TryParse(SelectedMaxResults, out var m) ? m : 500,
            Severities = BuildSeverityFilter()
        };

        try
        {
            const int batchSize = 50;
            var batch = new List<FriendlyEventEntry>(batchSize);

            await foreach (var entry in _eventLogs.ReadAsync(opt, _cts.Token))
            {
                batch.Add(entry);
                if (batch.Count >= batchSize)
                {
                    var items = batch.ToArray();
                    batch.Clear();
                    Post(() =>
                    {
                        foreach (var item in items)
                        {
                            Entries.Add(item);
                            UpdateCounts(item, 1);
                        }
                    });
                }
            }

            // Flush remaining items
            if (batch.Count > 0)
            {
                var remaining = batch.ToArray();
                Post(() =>
                {
                    foreach (var item in remaining)
                    {
                        Entries.Add(item);
                        UpdateCounts(item, 1);
                    }
                });
            }

            StatusMessage = $"Loaded {Entries.Count} events from {SelectedLog}";
            UpdateVisibleCount();
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = "Access denied: " + ex.Message; }
        catch (System.Diagnostics.Eventing.Reader.EventLogException ex) { StatusMessage = "Event log error: " + ex.Message; }
        catch (InvalidOperationException ex) { StatusMessage = "Error: " + ex.Message; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", LogFolder) { UseShellExecute = true })?.Dispose();
        }
        catch (IOException ex) { StatusMessage = ex.Message; }
        catch (UnauthorizedAccessException ex) { StatusMessage = ex.Message; }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void OpenEventViewer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true })?.Dispose();
        }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (SelectedEntry is null) return;
        var e = SelectedEntry;
        var text = new StringBuilder()
            .AppendLine($"[{e.Timestamp:yyyy-MM-dd HH:mm:ss}] {e.SeverityLabel} — {e.ProviderName} (Event {e.EventId})")
            .AppendLine($"Log: {e.LogName}")
            .AppendLine()
            .AppendLine("Explanation:").AppendLine(e.Explanation)
            .AppendLine()
            .AppendLine("Recommended action:").AppendLine(e.Recommendation)
            .AppendLine()
            .AppendLine("Full message:").AppendLine(e.FullMessage)
            .ToString();
        try { Clipboard.SetText(text); StatusMessage = "Copied to clipboard"; }
        catch (System.Runtime.InteropServices.ExternalException ex) { StatusMessage = ex.Message; }
        catch (System.Threading.ThreadStateException ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"sysmanager-{SelectedLog}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
            sw.WriteLine("Timestamp,Severity,Log,Provider,EventId,Message,Explanation,Recommendation");
            foreach (var e in Entries)
            {
                sw.Write(Csv(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))); sw.Write(',');
                sw.Write(Csv(e.SeverityLabel)); sw.Write(',');
                sw.Write(Csv(e.LogName)); sw.Write(',');
                sw.Write(Csv(e.ProviderName)); sw.Write(',');
                sw.Write(e.EventId); sw.Write(',');
                sw.Write(Csv(e.Message)); sw.Write(',');
                sw.Write(Csv(e.Explanation)); sw.Write(',');
                sw.WriteLine(Csv(e.Recommendation));
            }
            StatusMessage = $"Exported {Entries.Count} events to {dlg.FileName}";
        }
        catch (IOException ex) { StatusMessage = "Export failed: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { StatusMessage = "Export failed: " + ex.Message; }
    }

    [RelayCommand]
    private void SearchOnline()
    {
        if (SelectedEntry is null) return;
        var q = Uri.EscapeDataString($"Event ID {SelectedEntry.EventId} {SelectedEntry.ProviderName}");
        try { Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={q}") { UseShellExecute = true })?.Dispose(); }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = ex.Message; }
    }

    // ---------- Helpers ----------

    private List<EventSeverity> BuildSeverityFilter()
    {
        List<EventSeverity> list = [];
        if (ShowCritical) list.Add(EventSeverity.Critical);
        if (ShowError) list.Add(EventSeverity.Error);
        if (ShowWarning) list.Add(EventSeverity.Warning);
        if (ShowInfo) list.Add(EventSeverity.Info);
        if (ShowVerbose) list.Add(EventSeverity.Verbose);
        // If nothing selected, still return the list — the query will match nothing,
        // which is the user's intent (no noise at all).
        return list;
    }

    private static DateTime? ResolveSince(string range) => range switch
    {
        "Last hour" => DateTime.Now.AddHours(-1),
        "Last 24 hours" => DateTime.Now.AddDays(-1),
        "Last 7 days" => DateTime.Now.AddDays(-7),
        "Last 30 days" => DateTime.Now.AddDays(-30),
        "All" => null,
        _ => DateTime.Now.AddDays(-1)
    };

    private void ResetCounts()
    {
        CriticalCount = 0; ErrorCount = 0; WarningCount = 0; InfoCount = 0;
    }

    private void UpdateCounts(FriendlyEventEntry e, int delta)
    {
        switch (e.Severity)
        {
            case EventSeverity.Critical: CriticalCount += delta; break;
            case EventSeverity.Error: ErrorCount += delta; break;
            case EventSeverity.Warning: WarningCount += delta; break;
            case EventSeverity.Info: InfoCount += delta; break;
        }
    }

    private void Post(Action action)
    {
        if (_sync is null || SynchronizationContext.Current == _sync) action();
        else _sync.Post(_ => action(), null);
    }

    private static string Csv(string? s)
    {
        s ??= "";
        if (s.AsSpan().IndexOfAny(CsvSpecials) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    [RelayCommand]
    private void ToggleHighlight(object? parameter)
    {
        if (parameter is FriendlyEventEntry entry)
            entry.IsHighlighted = !entry.IsHighlighted;
    }
}
