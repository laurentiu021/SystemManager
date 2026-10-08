// SysManager · RecentChangesViewModel — the Recent Changes tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Recent Changes: what changed on this PC lately and who changed it, in one list (#1507). Read-only.
/// </summary>
/// <remarks>
/// The answer to "it was fine last week, what changed?" was spread over SysManager's activity log, Windows' own
/// reliability history, New App Alerts and Settings Watchdog, and the Dashboard showed five lines of the first. One look
/// reads all of them for the longest period; the period and kind filters then only choose what is shown.
/// </remarks>
public sealed partial class RecentChangesViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    /// <summary>The periods the tab offers, as their chips read.</summary>
    internal static readonly IReadOnlyList<string> Periods = ["7 days", "30 days", "90 days"];

    /// <summary>The kinds the tab filters on, as their chips read. "All" shows every kind.</summary>
    internal static readonly IReadOnlyList<string> Categories = ["All", "SysManager", "Windows", "Programs", "Settings"];

    private readonly IRecentChangesService _service;
    private readonly INavigationService _navigation;
    private readonly CancellationTokenSource _cts = new();
    private RecentChangesLook? _look;
    private Task _looking = Task.CompletedTask;
    private bool _lookAgain;

    /// <summary>The days shown, newest first, each with its changes.</summary>
    public BulkObservableCollection<ChangeDayRow> Days { get; } = new();

    /// <summary>The kind chosen with the chips: "All", "SysManager", "Windows", "Programs" or "Settings".</summary>
    [ObservableProperty] private string _selectedCategory = "All";

    /// <summary>The period chosen with the chips: "7 days", "30 days" or "90 days".</summary>
    [ObservableProperty] private string _selectedPeriod = "7 days";

    /// <summary>The line counting what changed in the period, of every kind.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>The line counting crashes and programs that stopped responding, which System Logs has.</summary>
    [ObservableProperty] private string _problemsText = "";

    [ObservableProperty] private bool _hasProblems;

    /// <summary>What could not be read, and the note on a first look, one sentence each.</summary>
    [ObservableProperty] private string _notes = "";

    [ObservableProperty] private bool _hasNotes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private bool _hasChanges;

    /// <summary>True once the tab has looked, so "nothing changed" is never said before it has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasLooked;

    /// <summary>True when the tab has looked and has nothing to show for the period and kind chosen.</summary>
    public bool ShowEmpty => HasLooked && !HasChanges;

    [ObservableProperty] private string _emptyTitle = "";

    [ObservableProperty] private string _emptyMessage = "";

    /// <summary>
    /// True while the tab is on screen. Set by <see cref="MainWindowViewModel.SetActive"/>; becoming visible looks again,
    /// which is what keeps the list of installed programs from one look to the next.
    /// </summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>The look the tab started when it was last shown. Internal so a test can await it.</summary>
    internal Task ShownLook { get; private set; } = Task.CompletedTask;

    public RecentChangesViewModel(IRecentChangesService service, INavigationService navigation)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        StatusMessage = "Looking at what changed…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(LookAsync);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy)) RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCategoryChanged(string value) => Show();

    partial void OnSelectedPeriodChanged(string value) => Show();

    partial void OnIsActiveChanged(bool value)
    {
        // Not while the first look is running: the tab is shown the moment it is built, and that look is the fresh one.
        if (value && InitializationComplete.IsCompleted) ShownLook = LookAsync();
    }

    /// <summary>Looks again at every source. What F5 does on this tab.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LookAsync();

    private bool CanRefresh() => !IsBusy;

    /// <summary>Opens the tab a change's button, or the line about System Logs, names.</summary>
    [RelayCommand]
    private void OpenTab(string? navId)
    {
        if (!string.IsNullOrWhiteSpace(navId)) _navigation.GoTo(navId);
    }

    // One look at a time; one asked for meanwhile runs once that one ends, since it is the change the user came to see.
    private Task LookAsync()
    {
        if (IsBusy)
        {
            _lookAgain = true;
            return Task.CompletedTask;
        }
        return _looking = LookNowAsync();
    }

    private async Task LookNowAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Looking at what changed…";
        try
        {
            _look = await _service.LookAsync(RecentChangesService.LongestPeriodDays, _cts.Token).ConfigureAwait(true);
            Show();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;   // the tab was closed while it looked
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Recent Changes could not look");
            (EmptyTitle, EmptyMessage) = ("Could not look at what changed", $"SysManager could not look just now: {ex.Message}");
            HasLooked = true;
            StatusMessage = EmptyMessage;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }

        if (_lookAgain)
        {
            _lookAgain = false;
            await LookAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Shows the last look through the period and kind chosen.</summary>
    private void Show()
    {
        if (_look is not { } look) return;
        var days = PeriodDays(SelectedPeriod);
        var since = look.LookedAt.AddDays(-days);
        var inPeriod = look.Changes.Where(c => c.When >= since).ToList();
        var shown = inPeriod.Where(c => Matches(c, SelectedCategory)).ToList();

        Days.ReplaceWith(BuildDays(shown, look.LookedAt));
        HasChanges = shown.Count > 0;
        HasLooked = true;
        Summary = Summarize(inPeriod, days);

        var problems = look.Problems.Where(p => p.When >= since).ToList();
        ProblemsText = DescribeProblems(problems);
        HasProblems = ProblemsText.Length > 0;

        Notes = Describe(look);
        HasNotes = Notes.Length > 0;

        (EmptyTitle, EmptyMessage) = DescribeEmpty(look, days, SelectedCategory);
        StatusMessage = HasChanges
            ? $"Looked at {RecentChangesService.Time(look.LookedAt)}. {Summary}"
            : $"Looked at {RecentChangesService.Time(look.LookedAt)}. {EmptyTitle}.";
    }

    /// <summary>
    /// Writes the changes shown to a CSV the user picks a location for. The file goes only where the dialog is pointed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasChanges))]
    private async Task ExportCsvAsync()
    {
        if (_look is not { } look) return;
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-RecentChanges-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var since = look.LookedAt.AddDays(-PeriodDays(SelectedPeriod));
        var shown = look.Changes.Where(c => c.When >= since && Matches(c, SelectedCategory)).ToList();
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ToCsv(shown), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Exported {shown.Count} change(s) to {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("Recent changes exported", Path.GetFileName(dlg.FileName));
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
    }

    // ── Wording, pure so it can be pinned ─────────────────────────────────

    /// <summary>The number of days a period chip stands for.</summary>
    internal static int PeriodDays(string period) =>
        int.TryParse(period.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : 7;

    /// <summary>Whether a change is shown under the kind chosen.</summary>
    internal static bool Matches(ChangeEvent change, string category) =>
        category == "All" || string.Equals(change.Category.ToString(), category, StringComparison.Ordinal);

    private static bool IsInstalledUpdate(ChangeKind kind) =>
        kind is ChangeKind.WindowsUpdate or ChangeKind.StoreAppUpdate or ChangeKind.DefenderUpdate or ChangeKind.DriverUpdate;

    /// <summary>
    /// The days, newest first, each with its changes newest first. Two or more updates Windows installed on one day are
    /// one row that opens to the list, so a night of updates does not bury everything else.
    /// </summary>
    internal static IReadOnlyList<ChangeDayRow> BuildDays(IReadOnlyList<ChangeEvent> changes, DateTime now)
    {
        var days = new List<ChangeDayRow>();
        foreach (var day in changes.GroupBy(c => c.When.Date).OrderByDescending(g => g.Key))
        {
            var updates = day.Where(c => IsInstalledUpdate(c.Kind)).OrderByDescending(c => c.When).ToList();
            var grouped = updates.Count >= 2;
            var rows = new List<(DateTime When, ChangeRow Row)>();
            if (grouped)
                rows.Add((updates[0].When, ChangeRow.ForUpdates(updates)));
            foreach (var change in day.Where(c => !grouped || !IsInstalledUpdate(c.Kind)))
                rows.Add((change.When, ChangeRow.For(change)));
            days.Add(new ChangeDayRow(DayHeader(day.Key, now), [.. rows.OrderByDescending(r => r.When).Select(r => r.Row)]));
        }
        return days;
    }

    /// <summary>A day's heading: "Today", "Yesterday", or the date.</summary>
    internal static string DayHeader(DateTime day, DateTime now) =>
        day.Date == now.Date ? "Today"
        : day.Date == now.Date.AddDays(-1) ? "Yesterday"
        : RecentChangesService.Day(day);

    /// <summary>The line counting what changed in the period, every kind, or empty when nothing did.</summary>
    internal static string Summarize(IReadOnlyList<ChangeEvent> inPeriod, int days)
    {
        var parts = new List<string>();
        Add(inPeriod.Count(c => IsInstalledUpdate(c.Kind)), "Windows installed 1 update", "Windows installed {0} updates");
        Add(inPeriod.Count(c => c.Kind == ChangeKind.UpdateFailed), "1 update could not be installed", "{0} updates could not be installed");
        Add(inPeriod.Count(c => c.Kind is ChangeKind.ProgramInstalled or ChangeKind.ProgramAppeared or ChangeKind.ProgramDetected),
            "1 program was installed", "{0} programs were installed");
        Add(inPeriod.Count(c => c.Kind == ChangeKind.ProgramUpdated), "1 program was updated", "{0} programs were updated");
        Add(inPeriod.Count(c => c.Kind is ChangeKind.ProgramRemoved or ChangeKind.ProgramDisappeared),
            "1 program was removed", "{0} programs were removed");
        Add(inPeriod.Count(c => c.Kind == ChangeKind.SysManagerAction), "SysManager made 1 change", "SysManager made {0} changes");
        Add(inPeriod.Count(c => c.Kind == ChangeKind.SettingChanged), "1 setting was changed", "{0} settings were changed");
        return parts.Count == 0 ? "" : $"In the last {days} days · {string.Join(" · ", parts)}";

        void Add(int count, string one, string many)
        {
            if (count == 1) parts.Add(one);
            else if (count > 1) parts.Add(string.Format(CultureInfo.InvariantCulture, many, count));
        }
    }

    /// <summary>The line about problems in the period, which System Logs lists, or empty when there were none.</summary>
    internal static string DescribeProblems(IReadOnlyList<ProblemEvent> problems)
    {
        var crashes = problems.Count(p => !p.StoppedResponding);
        var hangs = problems.Count(p => p.StoppedResponding);
        var parts = new List<string>();
        if (crashes > 0) parts.Add(crashes == 1 ? "1 app crash" : $"{crashes} app crashes");
        if (hangs > 0) parts.Add(hangs == 1 ? "1 app stopped responding" : $"{hangs} apps stopped responding");
        return parts.Count == 0 ? "" : $"Problems in the same days — {string.Join(", ", parts)} — are in System Logs.";
    }

    /// <summary>What could not be read, and what a first look means, one sentence each.</summary>
    internal static string Describe(RecentChangesLook look)
    {
        var said = new List<string>();
        foreach (var source in look.Unreadable)
        {
            said.Add(source switch
            {
                ChangeSource.WindowsHistory => "Windows' own history could not be read just now, so the updates and installs it records are missing. Refresh to try again.",
                ChangeSource.InstalledPrograms => "The list of installed programs could not be read just now, so some programs installed or removed may be missing. Refresh to try again.",
                ChangeSource.AppAlerts => "New App Alerts' list could not be read, so what it noticed is missing.",
                _ => "Settings Watchdog's record could not be read, so changed settings are missing.",
            });
        }
        if (look.FirstProgramsLook)
        {
            said.Add("SysManager keeps the list of installed programs from now on. From the next look, a program that appears "
                     + "or goes away in between is listed too, even one Windows keeps no record of.");
        }
        return string.Join(" ", said);
    }

    /// <summary>What the empty list says, for the period and kind chosen.</summary>
    internal static (string Title, string Message) DescribeEmpty(RecentChangesLook look, int days, string category)
    {
        if (category != "All")
            return ($"No changes of this kind in the last {days} days", "Choose All to see every kind of change, or a longer period.");

        var known = new List<string>();
        if (!look.Unreadable.Contains(ChangeSource.WindowsHistory)) known.Add("nothing was installed or updated");
        known.Add("SysManager changed nothing");
        if (look.HasBaseline && !look.Unreadable.Contains(ChangeSource.SettingsWatchdog)) known.Add("your saved settings still match");
        var sentence = known.Count switch
        {
            1 => known[0],
            2 => $"{known[0]} and {known[1]}",
            _ => $"{string.Join(", ", known.Take(known.Count - 1))}, and {known[^1]}",
        };
        return ($"No changes in the last {days} days",
                $"{char.ToUpperInvariant(sentence[0])}{sentence[1..]}. Pick a longer period above to look further back.");
    }

    /// <summary>The changes as CSV, with a header row: when, kind, what, who, and the line under it.</summary>
    internal static string ToCsv(IEnumerable<ChangeEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sb = new StringBuilder();
        Csv.AppendRow(sb, "When", "Kind", "What", "Who", "Detail");
        foreach (var c in changes)
        {
            var when = c.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (c.Since is { } since)
                when = $"between {since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} and {when}";
            Csv.AppendRow(sb, when, c.Category.ToString(), ChangeRow.TitleOf(c), c.Who, c.Detail);
        }
        return sb.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException ex)
            {
                Log.Debug(ex, "Recent Changes: calling off the look threw");
            }
            finally
            {
                _cts.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>One day on Recent Changes, with its changes.</summary>
public sealed class ChangeDayRow(string header, IReadOnlyList<ChangeRow> rows)
{
    /// <summary>"Today", "Yesterday", or the date.</summary>
    public string Header { get; } = header;

    public IReadOnlyList<ChangeRow> Rows { get; } = rows;
}

/// <summary>One row on Recent Changes: a change, or the updates Windows installed on one day.</summary>
public sealed partial class ChangeRow : ObservableObject
{
    private ChangeRow(string time, string glyph, string title, string who, string detail, ChangeLink link,
                      IReadOnlyList<string> items)
    {
        Time = time;
        Glyph = glyph;
        Title = title;
        Who = who;
        Detail = detail;
        Link = link;
        Items = items;
    }

    /// <summary>
    /// The time of day, "15:20", or a dash for a changed setting: when it changed is not known, only when Settings
    /// Watchdog noticed, which the line under it says.
    /// </summary>
    public string Time { get; }

    /// <summary>The icon, a Segoe Fluent Icons codepoint present in Segoe MDL2 Assets too.</summary>
    public string Glyph { get; }

    public string Title { get; }

    /// <summary>Who made the change, in words.</summary>
    public string Who { get; }

    /// <summary>The line under it, or empty.</summary>
    public string Detail { get; }

    public ChangeLink Link { get; }

    /// <summary>For the updates of one day, each update's name; otherwise empty.</summary>
    public IReadOnlyList<string> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>Whether the list of a day's updates is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText), nameof(ExpandName))]
    private bool _isExpanded;

    /// <summary>What the toggle for a day's updates reads.</summary>
    public string ExpandText => IsExpanded ? "Hide the list" : $"Show all {Items.Count}";

    /// <summary>The toggle's name for a screen reader, which says what it opens.</summary>
    public string ExpandName => IsExpanded ? "Hide the list of updates" : $"Show all {Items.Count} updates";

    /// <summary>Opens or closes the list of a day's updates.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public bool HasLink => Link != ChangeLink.None;

    /// <summary>What the button says.</summary>
    public string LinkText => Link switch
    {
        ChangeLink.Uninstaller => "Open Uninstaller",
        ChangeLink.AppAlerts => "Open App Alerts",
        ChangeLink.SettingsWatchdog => "Review",
        _ => "",
    };

    /// <summary>The tab the button opens.</summary>
    public string LinkNavId => Link switch
    {
        ChangeLink.Uninstaller => "nav-uninstaller",
        ChangeLink.AppAlerts => "nav-app-alerts",
        ChangeLink.SettingsWatchdog => "nav-settings-watchdog",
        _ => "",
    };

    /// <summary>The button's name for a screen reader, which the button's few words alone do not give.</summary>
    public string LinkName => $"{LinkText}: {Title}";

    /// <summary>The row for one change.</summary>
    internal static ChangeRow For(ChangeEvent change) => new(
        change.Kind == ChangeKind.SettingChanged ? "—" : RecentChangesService.Time(change.When), GlyphOf(change.Kind),
        TitleOf(change), change.Who, change.Detail, change.Link, []);

    /// <summary>The one row for the updates Windows installed on a day, newest first, which opens to their names.</summary>
    internal static ChangeRow ForUpdates(IReadOnlyList<ChangeEvent> updates) => new(
        RecentChangesService.Time(updates[0].When), "\uE895", $"Windows installed {updates.Count} updates",
        updates.Any(u => u.Kind == ChangeKind.StoreAppUpdate) && updates.All(u => u.Kind == ChangeKind.StoreAppUpdate)
            ? "Microsoft Store" : "Windows Update",
        Breakdown(updates), ChangeLink.None, [.. updates.Select(TitleOf)]);

    /// <summary>What a day's updates were, by kind: "6 app updates from the Microsoft Store, 1 driver".</summary>
    internal static string Breakdown(IReadOnlyList<ChangeEvent> updates)
    {
        var parts = new List<string>();
        Add(ChangeKind.StoreAppUpdate, "1 app update from the Microsoft Store", "{0} app updates from the Microsoft Store");
        Add(ChangeKind.WindowsUpdate, "1 Windows update", "{0} Windows updates");
        Add(ChangeKind.DefenderUpdate, "1 Defender definitions update", "{0} Defender definitions updates");
        Add(ChangeKind.DriverUpdate, "1 driver", "{0} drivers");
        return string.Join(", ", parts);

        void Add(ChangeKind kind, string one, string many)
        {
            var count = updates.Count(u => u.Kind == kind);
            if (count == 1) parts.Add(one);
            else if (count > 1) parts.Add(string.Format(CultureInfo.InvariantCulture, many, count));
        }
    }

    /// <summary>What a change's row says, in plain words.</summary>
    internal static string TitleOf(ChangeEvent change) => change.Kind switch
    {
        ChangeKind.SysManagerAction => change.Subject,
        ChangeKind.WindowsUpdate => $"Windows installed an update: {change.Subject}",
        ChangeKind.StoreAppUpdate => $"An app was updated from the Microsoft Store: {change.Subject}",
        ChangeKind.DefenderUpdate => "Microsoft Defender's virus definitions were updated",
        ChangeKind.DriverUpdate => $"A driver was updated: {change.Subject}",
        ChangeKind.UpdateFailed => $"An update could not be installed: {change.Subject}",
        ChangeKind.ProgramInstalled => $"Installed: {change.Subject}",
        ChangeKind.ProgramRemoved or ChangeKind.ProgramDisappeared => $"Removed: {change.Subject}",
        ChangeKind.ProgramUpdated => $"Updated: {change.Subject}",
        ChangeKind.ProgramAppeared or ChangeKind.ProgramDetected => $"New program: {change.Subject}",
        ChangeKind.SettingChanged => $"{change.Subject} changed",
        _ => change.Subject,
    };

    /// <summary>
    /// The icon for a kind of change. Each codepoint is one the app already draws elsewhere, so it is present in Segoe
    /// Fluent Icons and in Segoe MDL2 Assets, which Windows 10 falls back to.
    /// </summary>
    internal static string GlyphOf(ChangeKind kind) => kind switch
    {
        ChangeKind.SysManagerAction => "\uE713",
        ChangeKind.DriverUpdate => "\uE950",
        ChangeKind.UpdateFailed => "\uE7BA",
        ChangeKind.ProgramInstalled or ChangeKind.ProgramAppeared or ChangeKind.ProgramDetected
            or ChangeKind.ProgramUpdated => "\uE896",
        ChangeKind.ProgramRemoved or ChangeKind.ProgramDisappeared => "\uE74D",
        ChangeKind.SettingChanged => "\uE9D9",
        _ => "\uE895",
    };
}
