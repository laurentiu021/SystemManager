// SysManager · LogsViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Friendly Windows Event Log browser. Loads entries asynchronously, filters
/// live via a CollectionView, and shows a plain-English explanation for the
/// selected entry. Supports export to CSV and jumping to the raw log file.
/// </summary>
public partial class LogsViewModel : ViewModelBase
{
    private readonly EventLogService _eventLogs = new();
    private readonly SynchronizationContext? _sync;
    private CancellationTokenSource? _cts;

    public ObservableCollection<FriendlyEventEntry> Entries { get; } = new();
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

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FriendlyEventEntry? _selectedEntry;

    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;

    [ObservableProperty] private string _logFolder = LogService.LogDir;

    public LogsViewModel()
    {
        _sync = SynchronizationContext.Current;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = EntryFilter;
    }

    // ---------- Filter changes refresh the view ----------

    partial void OnShowCriticalChanged(bool value) => EntriesView.Refresh();
    partial void OnShowErrorChanged(bool value) => EntriesView.Refresh();
    partial void OnShowWarningChanged(bool value) => EntriesView.Refresh();
    partial void OnShowInfoChanged(bool value) => EntriesView.Refresh();
    partial void OnShowVerboseChanged(bool value) => EntriesView.Refresh();
    partial void OnSearchTextChanged(string value) => EntriesView.Refresh();

    private bool EntryFilter(object o)
    {
        if (o is not FriendlyEventEntry e) return false;

        var sevOk = e.Severity switch
        {
            EventSeverity.Critical => ShowCritical,
            EventSeverity.Error    => ShowError,
            EventSeverity.Warning  => ShowWarning,
            EventSeverity.Info     => ShowInfo,
            EventSeverity.Verbose  => ShowVerbose,
            _ => true
        };
        if (!sevOk) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
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
            await foreach (var entry in _eventLogs.ReadAsync(opt, _cts.Token))
            {
                Post(() =>
                {
                    Entries.Add(entry);
                    UpdateCounts(entry, 1);
                });
            }
            StatusMessage = $"Loaded {Entries.Count} events from {SelectedLog}";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled"; }
        catch (Exception ex) { StatusMessage = "Error: " + ex.Message; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", LogFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void OpenEventViewer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (SelectedEntry == null) return;
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
        catch (Exception ex) { StatusMessage = ex.Message; }
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
        catch (Exception ex) { StatusMessage = "Export failed: " + ex.Message; }
    }

    [RelayCommand]
    private void SearchOnline()
    {
        if (SelectedEntry == null) return;
        var q = Uri.EscapeDataString($"Event ID {SelectedEntry.EventId} {SelectedEntry.ProviderName}");
        try { Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={q}") { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    // ---------- Helpers ----------

    private List<EventSeverity> BuildSeverityFilter()
    {
        var list = new List<EventSeverity>();
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
            case EventSeverity.Error:    ErrorCount += delta; break;
            case EventSeverity.Warning:  WarningCount += delta; break;
            case EventSeverity.Info:     InfoCount += delta; break;
        }
    }

    private void Post(Action action)
    {
        if (_sync == null || SynchronizationContext.Current == _sync) action();
        else _sync.Post(_ => action(), null);
    }

    private static string Csv(string? s)
    {
        s ??= "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
