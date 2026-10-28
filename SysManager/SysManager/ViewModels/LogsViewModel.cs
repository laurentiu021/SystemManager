// SysManager · LogsViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
public sealed partial class LogsViewModel : ViewModelBase, IArrivalDestination
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly EventLogService _eventLogs;
    private readonly Func<EventLogQueryOptions, CancellationToken, IAsyncEnumerable<FriendlyEventEntry>> _readEvents;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<FriendlyEventEntry> Entries { get; } = new();
    public ICollectionView EntriesView { get; }

    public string[] AvailableLogs { get; } = ["System", "Application", "Security", "Setup"];

    /// <summary>The time ranges the tab offers, each with how far back it reaches, or null for the whole log.</summary>
    private static readonly (string Name, TimeSpan? Reach)[] Ranges =
    [
        ("Last hour", TimeSpan.FromHours(1)),
        ("Last 24 hours", TimeSpan.FromDays(1)),
        ("Last 7 days", TimeSpan.FromDays(7)),
        ("Last 30 days", TimeSpan.FromDays(30)),
        // Recent Changes' longest period, whose problems line opens this tab on it (#2887).
        ("Last 90 days", TimeSpan.FromDays(90)),
        ("All", null),
    ];

    public string[] TimeRanges { get; } = [.. Ranges.Select(r => r.Name)];
    public string[] MaxResultOptions { get; } = ["200", "500", "1000", "5000"];

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

    /// <summary>
    /// Fills the detail pane's XML when a row is selected.
    /// </summary>
    /// <remarks>
    /// The reader no longer renders XML for every record — it used to, for up to 5000 rows per refresh, to
    /// populate a field only the selected row displays. Fetched here instead, once per row: the guard on
    /// IsNullOrEmpty makes re-selecting a row free, and a row whose event has since rolled out of the log
    /// simply gets an empty pane rather than an error, which is the honest answer for a ring buffer.
    /// <para>Fire-and-forget because this hook is void and runs on the UI thread. The await resumes on the
    /// captured context, so assigning to the bound property is safe, and the captured entry is re-checked
    /// against the current selection so a fast click-through cannot write one row's XML onto another.</para>
    /// </remarks>
    partial void OnSelectedEntryChanged(FriendlyEventEntry? value)
    {
        if (value is null || !string.IsNullOrEmpty(value.Xml)) return;

        _ = FillXmlAsync(value);
    }

    private async Task FillXmlAsync(FriendlyEventEntry entry)
    {
        var xml = await _eventLogs.GetXmlAsync(entry.LogName, entry.RecordId).ConfigureAwait(true);
        if (ReferenceEquals(SelectedEntry, entry))
            entry.Xml = xml;
    }

    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;

    [ObservableProperty] private string _logFolder = LogService.LogDir;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private bool _hasNoResults;
    /// <summary>
    /// How many rows the user has marked. Drives the visibility of the "Clear marks" button — with no
    /// marks there is nothing to clear, and a permanently visible dead control is the kind of thing
    /// this fix exists to remove.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearMarksText))]
    private int _highlightedCount;

    /// <summary>
    /// The "Clear marks" button, with the count: "Clear 3 marks", or "Clear 1 mark". It was a <c>StringFormat</c> on
    /// <c>Content</c>, which WPF does not format, so the button read a bare "3" (#2808).
    /// </summary>
    public string ClearMarksText => HighlightedCount switch
    {
        1 => "Clear 1 mark",
        var n => $"Clear {n} marks",
    };

    // True when the log itself could not be read (access denied / missing / unavailable), as
    // opposed to being read and yielding nothing. Drives a separate empty state, because
    // "no events match your filters" is misleading when the filters never ran.
    [ObservableProperty] private bool _loadWasRefused;

    // The Security log is readable only when elevated — the banner names that and offers the
    // one-click relaunch, instead of only a status-line sentence with no button (#2859).
    [ObservableProperty] private bool _isElevated;

    public LogsViewModel(EventLogService eventLogs)
        : this(eventLogs, eventLogs.ReadAsync)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="readEvents"/> stands in for <see cref="EventLogService.ReadAsync"/>, so a test
    /// can feed the load a known number of events without reading this machine's event logs.
    /// </summary>
    internal LogsViewModel(
        EventLogService eventLogs,
        Func<EventLogQueryOptions, CancellationToken, IAsyncEnumerable<FriendlyEventEntry>> readEvents)
    {
        _eventLogs = eventLogs;
        _readEvents = readEvents;
        IsElevated = AdminHelper.IsElevated();
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = EntryFilter;
        // Disable Refresh while a scan runs — a second concurrent Refresh lets the
        // cancelled run's queued UI batches pollute the new Entries list. IsBusy lives
        // in the base class, so observe it here to re-evaluate the command.
        PropertyChanged += OnVmPropertyChanged;
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
            RefreshCommand.NotifyCanExecuteChanged();
    }

    private void UpdateVisibleCount()
    {
        // PERF-002: Use CollectionView.Count directly instead of iterating
        // the entire filtered collection via Cast<object>().Count().
        VisibleCount = (EntriesView as CollectionView)?.Count ?? EntriesView.Cast<object>().Count();
        HasNoResults = Entries.Count > 0 && VisibleCount == 0;
    }

    // ---------- Filter changes refresh the view ----------

    partial void OnShowCriticalChanged(bool value) => OnSeverityChanged(EventSeverity.Critical, value);
    partial void OnShowErrorChanged(bool value) => OnSeverityChanged(EventSeverity.Error, value);
    partial void OnShowWarningChanged(bool value) => OnSeverityChanged(EventSeverity.Warning, value);
    partial void OnShowInfoChanged(bool value) => OnSeverityChanged(EventSeverity.Info, value);
    partial void OnShowVerboseChanged(bool value) => OnSeverityChanged(EventSeverity.Verbose, value);
    partial void OnFilterTextChanged(string value) { EntriesView.Refresh(); UpdateVisibleCount(); }

    /// <summary>
    /// The severities the listed events were asked for, or null before the first load.
    /// </summary>
    /// <remarks>
    /// The severity switches do two jobs: a load asks Windows only for the severities switched on, and the switches
    /// then filter what it listed. Info and Verbose start off, so switching either on after a load filtered a list
    /// that held none of them, and nothing appeared until Refresh (#2601). Switching on a severity the load did not
    /// ask for now loads the list again. Switching one off stays a filter, because its events are already listed.
    /// </remarks>
    private HashSet<EventSeverity>? _loadedSeverities;

    private void OnSeverityChanged(EventSeverity severity, bool shown)
    {
        EntriesView.Refresh();
        UpdateVisibleCount();
        // While a load runs the command cannot start again; that load sees the switch when it ends. Not while an
        // arrival sets the switches either: it loads once, when they are all set.
        if (!_arriving && shown && NotLoaded(severity) && RefreshCommand.CanExecute(null))
            RefreshCommand.Execute(null);
    }

    /// <summary>True when the listed events come from a load that did not ask for <paramref name="severity"/>.</summary>
    private bool NotLoaded(EventSeverity severity) => _loadedSeverities is { } loaded && !loaded.Contains(severity);

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

    /// <summary>
    /// The sentence shown under the list once a load finishes.
    /// </summary>
    /// <remarks>
    /// Distinguishes "nothing matched" from "not allowed to look". The Security log is readable only by an
    /// elevated process, and the reader used to swallow that refusal and return an empty sequence — so a
    /// standard user selecting Security saw "Loaded 0 events", then the empty-state's "No events match your
    /// filters", with no way to know the filters were never applied to anything.
    /// <para>The partial case is the newer one. A read that ends early because the reader faulted keeps the
    /// records it already emitted, and the count-only wording announced those as a finished load. Saying
    /// "Loaded 137 events" about a list that stopped halfway is the same mistake as calling a cancelled scan
    /// complete, so it now says the list may be incomplete.</para>
    /// <para>Pure and internal so it can be tested directly: forcing an outcome through the real service is
    /// not possible from a test, and this is the sentence the user actually reads.</para>
    /// </remarks>
    internal static string DescribeLoad(int count, EventLogService.ReadOutcome outcome, string logName)
        => (count, outcome) switch
        {
            (0, EventLogService.ReadOutcome.AccessDenied) =>
                $"Windows would not let SysManager read the {logName} log. This log requires "
                + "administrator rights — restart SysManager as administrator to view it.",
            (0, EventLogService.ReadOutcome.LogNotFound) =>
                $"The {logName} log does not exist on this machine.",
            (0, EventLogService.ReadOutcome.Unavailable) =>
                $"The {logName} log could not be opened. It may be disabled or in use.",
            ( > 0, EventLogService.ReadOutcome.Unavailable) =>
                $"Loaded {count} events from {logName}, then the log stopped responding — this list is "
                + "incomplete. Try again to see the rest.",
            _ => $"Loaded {count} events from {logName}"
        };

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        var finished = await LoadAsync().ConfigureAwait(true);
        // A severity switched on while that load ran was not asked for by it, so the list loads once more with it. An
        // arrival while it ran stopped it, to load the list the arrival chose.
        while (TakeWaitingArrival() || (finished && BuildSeverityFilter().Exists(NotLoaded)))
            finished = await LoadAsync().ConfigureAwait(true);
    }

    // True while Arrive sets the log, the range and the switches, so a switch it turns on does not start a load early.
    private bool _arriving;

    // An arrival that came while a load ran. That load was stopped, and RefreshAsync loads the arrival's list next.
    private bool _arrivalWaiting;

    private bool TakeWaitingArrival()
    {
        var waiting = _arrivalWaiting;
        _arrivalWaiting = false;
        return waiting;
    }

    /// <summary>Opens on the log, time range and severities a link counted, and loads them (#2887).</summary>
    /// <remarks>
    /// The Dashboard's Event Log alert and Recent Changes' problems line sent the user here with nothing but the tab,
    /// which starts on the System log over 24 hours and reads nothing until Refresh: an empty grid, and after Refresh
    /// still not what either had counted. The search box is cleared too, because text typed earlier would hide what the
    /// link counted. A load already running reads what was chosen before, so it is stopped, and the arrival's list
    /// loads as soon as it has.
    /// </remarks>
    public void Arrive(NavigationArrival arrival)
    {
        if (arrival is not LogsArrival logs || !AvailableLogs.Contains(logs.Log)) return;

        _arriving = true;
        try
        {
            SelectedLog = logs.Log;
            SelectedTimeRange = RangeCovering(logs.Within);
            ShowCritical = logs.Severities.Contains(EventSeverity.Critical);
            ShowError = logs.Severities.Contains(EventSeverity.Error);
            ShowWarning = logs.Severities.Contains(EventSeverity.Warning);
            ShowInfo = logs.Severities.Contains(EventSeverity.Info);
            ShowVerbose = logs.Severities.Contains(EventSeverity.Verbose);
            FilterText = "";
        }
        finally
        {
            _arriving = false;
        }

        if (RefreshCommand.CanExecute(null))
        {
            RefreshCommand.Execute(null);
            return;
        }

        _arrivalWaiting = true;
        _cts?.Cancel();
    }

    /// <summary>
    /// Loads the list with what is chosen now. True when the load ran to its end, false when it was cancelled or
    /// failed.
    /// </summary>
    /// <remarks>
    /// An event the load lists again gets back its mark, and stays open in the detail pane (#2601). Switching a
    /// severity on loads the list again, and a load builds new rows, so without this a switch the user took for a
    /// filter would have dropped every mark and closed the event they were reading. Refresh dropped them too.
    /// </remarks>
    private async Task<bool> LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading events…";
        // Clear before loading so a previous refusal cannot keep the overlay up over a
        // successful reload (e.g. after the user elevates and switches back to Security).
        LoadWasRefused = false;
        HashSet<EventKey> marked = [];
        foreach (var row in Entries)
            if (row.IsHighlighted && EventKey.Of(row) is { } key) marked.Add(key);
        var open = SelectedEntry;
        Entries.Clear();
        ResetCounts();

        var opt = new EventLogQueryOptions
        {
            LogName = SelectedLog,
            Since = ResolveSince(SelectedTimeRange),
            MaxResults = int.TryParse(SelectedMaxResults, out var m) ? m : 500,
            Severities = BuildSeverityFilter()
        };
        _loadedSeverities = [.. opt.Severities];

        try
        {
            const int batchSize = 50;
            var batch = new List<FriendlyEventEntry>(batchSize);

            // UiThread.Post runs a batch inline on the UI thread, which is where this loop resumes. It asks the
            // dispatcher which thread it is on. The helper this replaced compared SynchronizationContext.Current
            // with the one captured at construction, and WPF installs a new context instance for every dispatcher
            // operation, so after the first await that comparison failed even on the UI thread. Every batch was
            // then queued, and the status line below was written before the last one ran: it under-counted, and
            // said "Loaded 0 events" for fewer than 50 (#2480).
            await foreach (var entry in _readEvents(opt, _cts.Token))
            {
                batch.Add(entry);
                if (batch.Count >= batchSize)
                {
                    var items = batch.ToArray();
                    batch.Clear();
                    UiThread.Post(() =>
                    {
                        foreach (var item in items)
                            AddLoaded(item, marked);
                    });
                }
            }

            // Flush remaining items
            if (batch.Count > 0)
            {
                var remaining = batch.ToArray();
                UiThread.Post(() =>
                {
                    foreach (var item in remaining)
                        AddLoaded(item, marked);
                });
            }

            StatusMessage = DescribeLoad(Entries.Count, _eventLogs.LastOutcome, SelectedLog);
            LoadWasRefused = Entries.Count == 0 && _eventLogs.LastOutcome != EventLogService.ReadOutcome.Ok;
            UpdateVisibleCount();
            return true;
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = "Access denied: " + ex.Message; }
        catch (System.Diagnostics.Eventing.Reader.EventLogException ex) { StatusMessage = "Event log error: " + ex.Message; }
        catch (InvalidOperationException ex) { StatusMessage = "Error: " + ex.Message; }
        finally
        {
            UpdateHighlightCount();
            Reopen(open);
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
        return false;
    }

    /// <summary>Lists an event the load read, marked when the same event was marked before it.</summary>
    private void AddLoaded(FriendlyEventEntry entry, HashSet<EventKey> marked)
    {
        if (EventKey.Of(entry) is { } key && marked.Contains(key)) entry.IsHighlighted = true;
        Entries.Add(entry);
        UpdateCounts(entry, 1);
    }

    /// <summary>
    /// Opens again the event that was open before the load, when the load listed it again and no other event was
    /// opened meanwhile.
    /// </summary>
    private void Reopen(FriendlyEventEntry? open)
    {
        // The grid lets go of its row when the list is cleared, and keeps a row picked while the load ran.
        if (open is null || (SelectedEntry is not null && !ReferenceEquals(SelectedEntry, open))) return;
        SelectedEntry = EventKey.Of(open) is { } key ? Entries.FirstOrDefault(e => EventKey.Of(e) == key) : null;
    }

    /// <summary>
    /// Which event a row shows, so a load that lists it again can tell. A record id is unique within its log. The
    /// time is there because a cleared log starts its record ids again, and the event that gets one is not the event
    /// that had it before.
    /// </summary>
    private readonly record struct EventKey(string Log, long Record, DateTime When)
    {
        /// <summary>The event <paramref name="e"/> shows, or null when it has no record id to tell it by.</summary>
        public static EventKey? Of(FriendlyEventEntry e)
            => e.RecordId > 0 ? new EventKey(e.LogName, e.RecordId, e.Timestamp) : null;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Cancel();
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
            Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"), LogFolder) { UseShellExecute = true })?.Dispose();
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
            Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("eventvwr.msc")) { UseShellExecute = true })?.Dispose();
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
            .AppendLine($"[{e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {e.SeverityLabel} — {e.ProviderName} (Event {e.EventId})")
            .AppendLine($"Log: {e.LogName}")
            .AppendLine()
            .AppendLine("Explanation:").AppendLine(e.Explanation)
            .AppendLine()
            .AppendLine("Recommended action:").AppendLine(e.Recommendation)
            .AppendLine()
            .AppendLine("Full message:").AppendLine(e.FullMessage)
            .ToString();
        StatusMessage = Helpers.ClipboardFeedback.TrySetText(text)
            ? "Copied to clipboard."
            : Helpers.ClipboardFeedback.Busy;
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"sysmanager-{SelectedLog}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            Csv.Write(dlg.FileName, ToCsv(Entries, CultureInfo.CurrentCulture));
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

    private static DateTime? ResolveSince(string range)
    {
        foreach (var (name, reach) in Ranges)
        {
            if (name == range) return reach is { } span ? DateTime.Now - span : null;
        }
        return DateTime.Now.AddDays(-1);
    }

    /// <summary>
    /// The shortest time range that reaches back <paramref name="within"/>, so an arrival shows all it counted.
    /// </summary>
    internal static string RangeCovering(TimeSpan within)
    {
        foreach (var (name, reach) in Ranges)
        {
            if (reach is not { } span || span >= within) return name;
        }
        return "All";
    }

    private void ResetCounts()
    {
        CriticalCount = 0; ErrorCount = 0; WarningCount = 0; InfoCount = 0;
        // Called right after Entries.Clear(), so every marked row is gone with it. Without this the
        // count would stay non-zero and keep offering to clear marks that no longer exist.
        HighlightedCount = 0;
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

    /// <summary>
    /// The loaded events as CSV with a header row: every event loaded, including any a filter is hiding.
    /// </summary>
    /// <remarks>
    /// Through <see cref="Csv"/>, like every other export (#2888). It had an escaper of its own and wrote commas by
    /// hand, so in a region that splits on semicolons a spreadsheet opened each row as one cell.
    /// </remarks>
    internal static string ToCsv(IEnumerable<FriendlyEventEntry> entries, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(culture);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, culture, "Timestamp", "Severity", "Log", "Provider", "EventId", "Message", "Explanation",
            "Recommendation");
        foreach (var e in entries)
        {
            Csv.AppendRow(sb, culture,
                e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.SeverityLabel,
                e.LogName,
                e.ProviderName,
                e.EventId.ToString(culture),
                e.Message,
                e.Explanation,
                e.Recommendation);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Marks or unmarks one event row, so a user reading through hundreds of events can keep the ones
    /// that matter findable. Bound from the grid's mark column.
    /// </summary>
    /// <remarks>
    /// The mark lives on the <see cref="FriendlyEventEntry"/> instance, and the severity checkboxes and
    /// search box filter through <see cref="EntriesView"/> — an <see cref="ICollectionView"/> over those
    /// same instances — so a mark survives filtering and column sorting. A load clears <see cref="Entries"/>
    /// and builds new ones; an event it lists again gets its mark back, and one it does not list, such as an
    /// event from another log, takes its mark with it (#2601).
    /// </remarks>
    [RelayCommand]
    private void ToggleHighlight(object? parameter)
    {
        if (parameter is not FriendlyEventEntry entry) return;
        entry.IsHighlighted = !entry.IsHighlighted;
        UpdateHighlightCount();
    }

    /// <summary>Clears every mark, so the user is never left hunting marked rows one at a time.</summary>
    [RelayCommand]
    private void ClearHighlights()
    {
        // Entries, not EntriesView: a mark can sit on a row the current severity/search filter hides,
        // and a "Clear marks" that left invisible marks behind would be its own small broken promise.
        foreach (var entry in Entries)
            entry.IsHighlighted = false;
        UpdateHighlightCount();
    }

    private void UpdateHighlightCount() => HighlightedCount = Entries.Count(e => e.IsHighlighted);
}
