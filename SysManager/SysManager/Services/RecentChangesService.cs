// SysManager · RecentChangesService — what changed on the PC lately, from every source that knows
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>A source Recent Changes reads, named when it could not be read.</summary>
public enum ChangeSource
{
    /// <summary>Windows' own history behind Reliability Monitor.</summary>
    WindowsHistory,

    /// <summary>The list of installed programs SysManager keeps from one look to the next.</summary>
    InstalledPrograms,

    /// <summary>What New App Alerts noticed.</summary>
    AppAlerts,

    /// <summary>When Settings Watchdog first saw each changed setting.</summary>
    SettingsWatchdog,
}

/// <summary>What one look found.</summary>
/// <param name="Changes">Everything that changed in the period, newest first.</param>
/// <param name="Problems">The programs that crashed or stopped responding in the period, for the line about System Logs.</param>
/// <param name="Unreadable">The sources that could not be read, so their changes are missing.</param>
/// <param name="FirstProgramsLook">True when this was the first look at the installed programs, so none could be new yet.</param>
/// <param name="HasBaseline">True when Settings Watchdog has a saved baseline the settings were compared with.</param>
/// <param name="LookedAt">When the look was made.</param>
/// <param name="ActivityKeptFrom">
/// When the activity log is full, the time of the oldest action it still holds: SysManager's actions before it are no
/// longer kept. Null while the log has room, so every action is there.
/// </param>
public sealed record RecentChangesLook(IReadOnlyList<ChangeEvent> Changes, IReadOnlyList<ProblemEvent> Problems,
                                       IReadOnlyList<ChangeSource> Unreadable, bool FirstProgramsLook, bool HasBaseline,
                                       DateTime LookedAt, DateTime? ActivityKeptFrom = null);

/// <summary>Everything that changed on the PC lately, in one list (#1507).</summary>
public interface IRecentChangesService
{
    /// <summary>
    /// What changed in the last <paramref name="days"/> days. Also keeps the list of installed programs and when each
    /// changed setting was first seen, so the next look can tell what is new.
    /// </summary>
    Task<RecentChangesLook> LookAsync(int days, CancellationToken ct = default);
}

/// <summary>
/// Reads SysManager's activity log, Windows' reliability history, the installed programs kept from the last look, New
/// App Alerts' detections and Settings Watchdog's sightings, and puts them in one list, each change once.
/// </summary>
/// <remarks>
/// A program can reach the list three ways, and each is listed once by the best of them: Windows Installer's own
/// record, with its exact time; then New App Alerts', with the time it noticed; then the difference between two looks,
/// which only says between which two times. Read-only towards Windows: nothing here changes anything on the PC.
/// </remarks>
public sealed class RecentChangesService : IRecentChangesService
{
    /// <summary>The longest period the tab offers. One look covers it, and the shorter periods filter it.</summary>
    public const int LongestPeriodDays = 90;

    /// <summary>
    /// How long the records SysManager keeps for this tab hold a change: past the longest period, so one at its edge is
    /// still there for a look a few weeks late, and no longer, so the records do not grow for good.
    /// </summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromDays(LongestPeriodDays + 30);

    /// <summary>
    /// How far apart two records of one install can be. An installer creates its folder minutes before it writes its
    /// uninstall entry, Windows Installer records the end, and New App Alerts checks the entries every 30 seconds.
    /// </summary>
    private static readonly TimeSpan SameInstall = TimeSpan.FromMinutes(10);

    private readonly IReliabilityHistory _reliability;
    private readonly IInstalledProgramsHistory _programs;
    private readonly IAppAlertHistory _alerts;
    private readonly ISettingsWatchdogService _watchdog;
    private readonly Func<IReadOnlyList<ActivityEntry>> _readActivity;
    private readonly TimeProvider _clock;

    /// <summary>The service production uses, reading the activity log every tab writes to.</summary>
    public RecentChangesService(IReliabilityHistory reliability, IInstalledProgramsHistory programs, IAppAlertHistory alerts,
                                ISettingsWatchdogService watchdog)
        : this(reliability, programs, alerts, watchdog,
               () => ActivityLogService.Instance.GetRecent(ActivityLogService.MaxEntries), TimeProvider.System)
    {
    }

    /// <summary>A service over fixed sources and a fixed clock, for a test.</summary>
    internal RecentChangesService(IReliabilityHistory reliability, IInstalledProgramsHistory programs, IAppAlertHistory alerts,
                                  ISettingsWatchdogService watchdog, Func<IReadOnlyList<ActivityEntry>> readActivity,
                                  TimeProvider clock)
    {
        _reliability = reliability ?? throw new ArgumentNullException(nameof(reliability));
        _programs = programs ?? throw new ArgumentNullException(nameof(programs));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _readActivity = readActivity ?? throw new ArgumentNullException(nameof(readActivity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<RecentChangesLook> LookAsync(int days, CancellationToken ct = default)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days), days, "A period is at least one day.");
        var now = _clock.GetLocalNow().DateTime;
        var since = now.AddDays(-days);

        // Windows' history is the slow one, so it is asked first and the local sources are read while it answers.
        var windows = _reliability.ReadAsync(since, ct);
        var local = await Task.Run(() => ReadLocal(now), ct).ConfigureAwait(false);
        var history = await windows.ConfigureAwait(false);

        var unreadable = new List<ChangeSource>();
        if (!history.Readable) unreadable.Add(ChangeSource.WindowsHistory);
        if (!local.Programs.Readable) unreadable.Add(ChangeSource.InstalledPrograms);
        if (!local.Alerts.Readable) unreadable.Add(ChangeSource.AppAlerts);
        if (!local.Sightings.Readable) unreadable.Add(ChangeSource.SettingsWatchdog);

        var (windowsChanges, problems) = ReliabilityChanges.Parse(history.Records);
        var changes = Combine(
            [.. Activity(local.Activity), .. windowsChanges],
            local.Alerts.Alerts.Select(Detected).ToList(),
            local.Programs.Changes.Select(Program).ToList(),
            Settings(local.Sightings.Sightings, _watchdog.Catalog));

        // The log keeps a number of actions, not a number of days, so when it is full its oldest may be inside the period.
        DateTime? activityKeptFrom = local.Activity.Count >= ActivityLogService.MaxEntries
            ? local.Activity.Min(e => e.Timestamp)
            : null;

        return new RecentChangesLook(
            [.. changes.Where(c => c.When >= since && c.When <= now)],
            [.. problems.Where(p => p.When >= since && p.When <= now)], unreadable, local.Programs.FirstLook,
            local.HasBaseline, now, activityKeptFrom);
    }

    private sealed record LocalSources(IReadOnlyList<ActivityEntry> Activity, ProgramsLook Programs, AppAlertsRead Alerts,
                                       DriftSightings Sightings, bool HasBaseline);

    private LocalSources ReadLocal(DateTime now)
    {
        var programs = _programs.Observe(now);
        var alerts = _alerts.Load();

        // Looking at the settings is what records a drift's first sighting; with no baseline there is nothing to drift from.
        var baseline = _watchdog.LoadBaseline();
        var sightings = baseline is null
            ? new DriftSightings([], Readable: true)
            : _watchdog.RecordDrift(_watchdog.DetectDrift(baseline, _watchdog.ReadCurrent()), now);

        return new LocalSources(_readActivity(), programs, alerts, sightings, baseline is not null);
    }

    /// <summary>
    /// Every change, newest first, with each program listed once: a Windows Installer record wins over New App Alerts'
    /// detection of the same program, and either wins over a difference between two looks that covers its time.
    /// </summary>
    internal static IReadOnlyList<ChangeEvent> Combine(IReadOnlyList<ChangeEvent> exact, IReadOnlyList<ChangeEvent> detected,
                                                       IReadOnlyList<ChangeEvent> betweenLooks, IReadOnlyList<ChangeEvent> settings)
    {
        var installed = exact.Where(c => c.Kind == ChangeKind.ProgramInstalled).ToList();
        var removed = exact.Where(c => c.Kind == ChangeKind.ProgramRemoved).ToList();

        var keptDetections = detected
            .Where(d => !installed.Any(i => SameName(i, d) && (i.When - d.When).Duration() <= SameInstall))
            .ToList();

        var keptDifferences = betweenLooks.Where(b => b.Kind switch
        {
            ChangeKind.ProgramDisappeared => !removed.Any(r => SameName(r, b) && Covers(b, r.When)),
            _ => !installed.Concat(keptDetections).Any(i => SameName(i, b) && Covers(b, i.When)),
        });

        return [.. exact.Concat(keptDetections).Concat(keptDifferences).Concat(settings).OrderByDescending(c => c.When)];

        static bool SameName(ChangeEvent a, ChangeEvent b) => string.Equals(a.Subject, b.Subject, StringComparison.OrdinalIgnoreCase);

        static bool Covers(ChangeEvent window, DateTime when) =>
            when <= window.When + SameInstall && (window.Since is not { } since || when >= since - SameInstall);
    }

    /// <summary>SysManager's own actions, in the words the Dashboard's Recent activity card uses.</summary>
    internal static IEnumerable<ChangeEvent> Activity(IReadOnlyList<ActivityEntry> entries) =>
        entries.Select(e => new ChangeEvent(e.Timestamp, null, ChangeKind.SysManagerAction,
            string.IsNullOrWhiteSpace(e.Detail) ? e.Action : e.Detail, "You, in SysManager",
            string.IsNullOrWhiteSpace(e.Detail) ? "" : e.Action));

    /// <summary>One of New App Alerts' detections: a new folder names where it appeared, a new uninstall entry does not.</summary>
    internal static ChangeEvent Detected(AppInstallEntry alert) => new(
        alert.DetectedAt, null, ChangeKind.ProgramDetected, alert.Name, "An installer",
        string.Equals(alert.Source, "FileSystem", StringComparison.Ordinal)
        && System.IO.Path.GetDirectoryName(alert.InstallPath) is { Length: > 0 } folder
            ? $"A new folder in {folder}, noticed by New App Alerts"
            : "Noticed by New App Alerts while it was watching");

    /// <summary>A difference between two looks at the installed programs, with the window it happened in.</summary>
    internal static ChangeEvent Program(ProgramChange change)
    {
        var window = $"some time between your look on {Day(change.Since)} and {Time(change.Until)}";
        return change.Kind switch
        {
            ChangeKind.ProgramDisappeared => new ChangeEvent(change.Until, change.Since, change.Kind, change.Name,
                "An uninstaller", $"Removed {window}"),
            ChangeKind.ProgramUpdated => new ChangeEvent(change.Until, change.Since, change.Kind, change.Name,
                "An installer", $"Was {change.Before}; updated {window}"),
            _ => new ChangeEvent(change.Until, change.Since, ChangeKind.ProgramAppeared, change.Name,
                "An installer", $"Installed {window}"),
        };
    }

    /// <summary>
    /// Each watched setting found changed from the saved baseline, at the look that first saw it. A sighting of a
    /// setting the catalog no longer has is left out: its name and values can no longer be said.
    /// </summary>
    internal static IReadOnlyList<ChangeEvent> Settings(IReadOnlyList<DriftSighting> sightings, IReadOnlyList<WatchedSetting> catalog)
    {
        var changes = new List<ChangeEvent>();
        foreach (var sighting in sightings)
        {
            if (catalog.FirstOrDefault(s => s.Key == sighting.Key) is not { } setting) continue;
            var detail = $"Found {setting.Describe(sighting.Value)}, where you saved {setting.Describe(sighting.Baseline)}. "
                         + $"Settings Watchdog noticed at {Time(sighting.FirstSeen)}."
                         + (sighting.GoneAt is not { } gone ? ""
                            : sighting.ChangedAgain ? $" It changed again on {Day(gone)}."
                            : $" It is back as you saved it since {Day(gone)}.");
            changes.Add(new ChangeEvent(sighting.FirstSeen, null, ChangeKind.SettingChanged, setting.Name,
                "Windows or another program", detail));
        }
        return changes;
    }

    /// <summary>A day as the rows say it: "Thu 2 Oct".</summary>
    internal static string Day(DateTime when) => when.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>A time of day as the rows say it: "15:20".</summary>
    internal static string Time(DateTime when) => when.ToString("HH:mm", CultureInfo.InvariantCulture);
}
