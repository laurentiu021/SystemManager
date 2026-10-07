// SysManager · UndoChangesService — finds the changes SysManager can put back, and puts one back
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Finds the changes SysManager can put back, from the copies five tabs already keep, and puts one back at a time
/// through the restore that tab itself uses (#1525).
/// </summary>
/// <remarks>
/// Every copy here was reachable only from the tab that wrote it, so undoing a change meant remembering which tab
/// had made it. This reads them all and words each one the same way, and it owns no file of its own: a copy that is
/// not there is a change that is not listed.
/// <para>A copy that is still there is not always a change that is still there. Two of them — the hosts file's and
/// the environment variables' — are kept for good, so they are compared with what is on the PC now, and a change
/// is listed only while putting it back would change something.</para>
/// <para>Each put-back takes the lock its tab takes, reads its copy again under it, and changes nothing unless what
/// it finds is still what the question described. Afterwards, whenever something may have been written, it tells the
/// tab that made the change, through <see cref="IPutBackSignal"/>, so that tab does not go on showing the change.</para>
/// </remarks>
public sealed class UndoChangesService : IUndoChangesService
{
    /// <summary>What the Performance Mode row is called — its sidebar name.</summary>
    internal const string PerformanceTitle = "Performance Mode";

    /// <summary>What the Services row is called — its sidebar name.</summary>
    internal const string ServicesTitle = "Services";

    /// <summary>What the hosts file row is called.</summary>
    internal const string HostsTitle = "Hosts file";

    /// <summary>What the environment variables row is called — its sidebar name.</summary>
    internal const string EnvironmentTitle = "Environment Variables";

    /// <summary>What the Gaming Profile row is called — its sidebar name.</summary>
    internal const string GamingTitle = "Gaming Profile";

    /// <summary>
    /// What the Settings Watchdog row is called. It does not say who changed them: Windows, another program, or the
    /// user, which is why Settings Watchdog asks before each one.
    /// </summary>
    internal const string WatchdogTitle = "Settings changed since you saved them";

    /// <summary>The tab the Settings Watchdog row opens, since each of its settings is reviewed there.</summary>
    internal const string WatchdogTab = "nav-settings-watchdog";

    /// <summary>
    /// How many lines a question lists before it says how many more there are. Restoring the variables can touch
    /// dozens of them, and a dialog taller than the screen hides its own buttons.
    /// </summary>
    internal const int MaxQuestionLines = 12;

    // The label the graphics line starts with, so the put-back can say a restart is needed for it.
    private const string GraphicsLabel = "NVIDIA graphics";

    private const string GameModeFirst =
        "Nothing was changed: game mode is on. Turn it off first — while it is on, the power plan and visual effects "
        + "are its own, and turning it off puts back the ones it started from.";

    private const string GameModeNotKnown =
        "Nothing was changed: SysManager could not read just now whether game mode was left on, and if it was, turning "
        + "it off later would put Performance Mode's changes back. Try again in a moment.";

    private readonly PerformanceService _performance;
    private readonly ServiceStartupLedgerService _ledger;
    private readonly IPowerShellRunner _powerShell;
    private readonly HostsFileService _hosts;
    private readonly EnvironmentVariableService _environment;
    private readonly IGamingProfileService _gaming;
    private readonly ISettingsWatchdogService _watchdog;
    private readonly RestorePointService _restorePoints;
    private readonly IPutBackSignal _signal;
    private readonly Func<bool> _isElevated;
    private readonly Func<string, ServiceEntry?> _readService;
    private readonly Func<CancellationToken, Task<PerformanceService.OriginalSnapshot>> _readPerformanceNow;
    private readonly Func<string, GraphicsNow> _readGraphics;
    private readonly Func<PerformanceService.OriginalSnapshot, CancellationToken, Task<bool>> _restorePerformance;
    private readonly Func<EnvironmentVariableService.RestoreResult> _restoreEnvironment;
    private readonly Action _announceEnvironment;
    private readonly TimeZoneInfo _zone;

    /// <summary>Creates the service over the tabs' own services, as the container supplies them.</summary>
    public UndoChangesService(
        PerformanceService performance,
        ServiceStartupLedgerService ledger,
        IPowerShellRunner powerShell,
        HostsFileService hosts,
        EnvironmentVariableService environment,
        IGamingProfileService gaming,
        ISettingsWatchdogService watchdog,
        RestorePointService restorePoints,
        IPutBackSignal signal)
        : this(performance, ledger, powerShell, hosts, environment, gaming, watchdog, restorePoints, signal,
            AdminHelper.IsElevated, ServiceManagerService.ReadEntry,
            readPerformanceNow: null, readGraphics: null, restorePerformance: null, restoreEnvironment: null,
            announceEnvironment: null, zone: null)
    {
    }

    /// <summary>
    /// Test seam. The defaults are the real calls; a test replaces the ones that would read or change this PC.
    /// </summary>
    /// <param name="readPerformanceNow">Reads the Performance Mode settings as they are now, in the shape of its record.</param>
    /// <param name="readGraphics">Reads the graphics setting on the adapter a Performance Mode record names, as it is now.</param>
    /// <param name="restorePerformance">Puts the Performance Mode record back and deletes it.</param>
    /// <param name="restoreEnvironment">Puts the kept copy of the variables back.</param>
    /// <param name="announceEnvironment">Tells every open program that the variables changed — a broadcast to every
    /// window on the desktop, which a test has no business sending.</param>
    /// <param name="zone">The time zone dates are written in.</param>
    internal UndoChangesService(
        PerformanceService performance,
        ServiceStartupLedgerService ledger,
        IPowerShellRunner powerShell,
        HostsFileService hosts,
        EnvironmentVariableService environment,
        IGamingProfileService gaming,
        ISettingsWatchdogService watchdog,
        RestorePointService restorePoints,
        IPutBackSignal signal,
        Func<bool> isElevated,
        Func<string, ServiceEntry?> readService,
        Func<CancellationToken, Task<PerformanceService.OriginalSnapshot>>? readPerformanceNow,
        Func<string, GraphicsNow>? readGraphics,
        Func<PerformanceService.OriginalSnapshot, CancellationToken, Task<bool>>? restorePerformance,
        Func<EnvironmentVariableService.RestoreResult>? restoreEnvironment,
        Action? announceEnvironment,
        TimeZoneInfo? zone)
    {
        _performance = performance;
        _ledger = ledger;
        _powerShell = powerShell;
        _hosts = hosts;
        _environment = environment;
        _gaming = gaming;
        _watchdog = watchdog;
        _restorePoints = restorePoints;
        _signal = signal;
        _isElevated = isElevated;
        _readService = readService;
        _readPerformanceNow = readPerformanceNow ?? performance.TakeSnapshotAsync;
        _readGraphics = readGraphics ?? (subKey => ReadGraphics(performance, subKey));
        _restorePerformance = restorePerformance ?? performance.RestoreOriginalAsync;
        _restoreEnvironment = restoreEnvironment ?? environment.RestoreFromBackup;
        _announceEnvironment = announceEnvironment ?? EnvironmentVariableService.BroadcastSettingChange;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    // What one copy holds now: the change it would put back, or nothing — and when nothing, whether that is because
    // the copy could not be used, or because Performance Mode waits for game mode to end, or might have to: whether
    // game mode was left on could not be read.
    private readonly record struct Found(
        UndoChangeKind Kind, UndoChange? Change, UndoProblem? Problem = null, bool WaitsForGameMode = false,
        bool GameModeNotKnown = false);

    private static Found Problem(UndoChangeKind kind, UndoProblemKind why) => new(kind, null, new UndoProblem(kind, why));

    /// <inheritdoc/>
    public async Task<UndoScan> ScanAsync(CancellationToken ct = default)
    {
        List<UndoChange> changes = [];
        List<UndoProblem> problems = [];
        var waitsForGameMode = false;

        void Keep(Found found)
        {
            if (found.Change is { } change) changes.Add(change);
            if (found.Problem is { } problem) problems.Add(problem);
            waitsForGameMode |= found.WaitsForGameMode;
        }

        Keep((await FindPerformanceAsync(ct).ConfigureAwait(false)).Found);
        Keep(FindServices().Found);
        Keep(FindHosts());
        Keep(FindEnvironment().Found);
        Keep(FindGaming());
        Keep(FindWatchdog());

        return new UndoScan(changes, problems, waitsForGameMode);
    }

    /// <inheritdoc/>
    public async Task<RestorePointLook> LookForRestorePointAsync(CancellationToken ct = default)
    {
        // Windows lists them only to an administrator; asking otherwise costs a PowerShell session to hear no.
        if (!_isElevated()) return new RestorePointLook(null, Listed: false);

        try
        {
            var points = await _restorePoints.ListAsync(ct).ConfigureAwait(false);
            return points is null
                ? new RestorePointLook(null, Listed: false)
                : new RestorePointLook(points.Count > 0 ? points[0] : null, Listed: true);
        }
        catch (InvalidOperationException ex)
        {
            // The runner that would ask could not start. Said as a refusal is: Windows did not answer.
            Log.Debug(ex, "Undo Changes could not ask Windows for its restore points");
            return new RestorePointLook(null, Listed: false);
        }
    }

    /// <inheritdoc/>
    public async Task<UndoOutcome> PutBackAsync(UndoChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.OpensTab is not null)
            return new UndoOutcome(UndoOutcomeKind.Failed,
                "Nothing was changed: these settings are put back in Settings Watchdog, one at a time.");

        // The lock its own tab's restore takes, taken first and held from reading the copy again to the end, so what is
        // put back is what was compared, and a refusal costs nothing.
        var operation = LockFor(change.Kind);
        using var opLock = operation is null
            ? null
            : OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, operation);
        if (operation is not null && opLock is null) return Busy();

        // Each kind reads its copy again here, under the lock, and puts back what that read found.
        return change.Kind switch
        {
            UndoChangeKind.PerformanceMode => await PutBackPerformanceAsync(change, ct).ConfigureAwait(false),
            UndoChangeKind.Services => await PutBackServicesAsync(change, ct).ConfigureAwait(false),
            UndoChangeKind.HostsFile => Refusal(change, FindHosts()) ?? PutBackHosts(),
            UndoChangeKind.EnvironmentVariables => PutBackEnvironment(change),
            UndoChangeKind.GamingProfile => Refusal(change, FindGaming()) ?? await PutBackGamingAsync(ct).ConfigureAwait(false),
            _ => new UndoOutcome(UndoOutcomeKind.Failed, $"Nothing was changed: {Name(change.Kind)} cannot be put back from here."),
        };
    }

    /// <summary>
    /// Why <paramref name="listed"/> is not put back, given what reading its copy again found, or null when it can be.
    /// </summary>
    private UndoOutcome? Refusal(UndoChange listed, Found found)
    {
        var name = Name(listed.Kind);
        if (found.Change is not { } now)
        {
            if (found.WaitsForGameMode)
                return new UndoOutcome(UndoOutcomeKind.Failed, found.GameModeNotKnown ? GameModeNotKnown : GameModeFirst);
            return found.Problem?.Why switch
            {
                null => new UndoOutcome(UndoOutcomeKind.NothingLeft, $"Nothing is left to put back for {name}."),
                UndoProblemKind.Damaged => new UndoOutcome(UndoOutcomeKind.Failed,
                    $"What SysManager kept for {name} is damaged, so nothing was changed."),
                UndoProblemKind.CannotCompare => new UndoOutcome(UndoOutcomeKind.Failed,
                    $"SysManager could not compare {name} with what it kept just now, so nothing was changed. Try again in "
                    + "a moment."),
                _ => new UndoOutcome(UndoOutcomeKind.Failed,
                    $"SysManager could not read what it kept for {name} just now, so nothing was changed. Try again in a moment."),
            };
        }

        // The list is read again after every put-back, so by the time this is on screen it shows the change as it is.
        if (!DescribesTheSame(listed, now))
            return new UndoOutcome(UndoOutcomeKind.ChangedSinceListed,
                $"Nothing was changed: {name} is no longer as this list showed. The list now shows it as it is.");

        if (now.NeedsAdmin && !_isElevated())
            return new UndoOutcome(UndoOutcomeKind.Failed,
                $"Putting back {name} needs administrator rights. Use Run as administrator at the top of the page.");

        return null;
    }

    /// <summary>
    /// True when <paramref name="now"/> still says what <paramref name="listed"/> said: the same change, the same
    /// lines, and the same need for administrator rights. The dates are not compared, because they come from the same
    /// copy.
    /// </summary>
    internal static bool DescribesTheSame(UndoChange listed, UndoChange now) =>
        listed.Kind == now.Kind
        && string.Equals(listed.Detail, now.Detail, StringComparison.Ordinal)
        && listed.NeedsAdmin == now.NeedsAdmin
        && listed.Lines.SequenceEqual(now.Lines, StringComparer.Ordinal);

    /// <summary>
    /// The operation each put-back takes the system-modification lock as — the one its own tab's restore takes — or
    /// null for the two that take none here. The hosts file's restore swaps a finished file into place, as the DNS &amp;
    /// Hosts tab's does (#2553), and game mode's reverts take the lock themselves when it is free and never refuse.
    /// </summary>
    private static string? LockFor(UndoChangeKind kind) => kind switch
    {
        UndoChangeKind.PerformanceMode => "Put back Performance Mode",
        UndoChangeKind.Services => "Turn services back on",
        UndoChangeKind.EnvironmentVariables => "Restore environment variables",
        _ => null,
    };

    /// <summary>What a kept copy is called in a sentence.</summary>
    internal static string Name(UndoChangeKind kind) => kind switch
    {
        UndoChangeKind.PerformanceMode => "Performance Mode",
        UndoChangeKind.Services => "the services",
        UndoChangeKind.HostsFile => "the hosts file",
        UndoChangeKind.EnvironmentVariables => "the environment variables",
        UndoChangeKind.GamingProfile => "game mode",
        _ => "Settings Watchdog",
    };

    // ── Performance Mode ───────────────────────────────────────────────────────

    private async Task<(Found Found, PerformanceService.OriginalSnapshot? Original)> FindPerformanceAsync(CancellationToken ct)
    {
        var original = _performance.LoadSnapshot(out var problem);
        if (original is null)
        {
            return (problem switch
            {
                PerformanceService.SnapshotProblem.None => new Found(UndoChangeKind.PerformanceMode, null),
                PerformanceService.SnapshotProblem.Invalid => Problem(UndoChangeKind.PerformanceMode, UndoProblemKind.Damaged),
                _ => Problem(UndoChangeKind.PerformanceMode, UndoProblemKind.Unreadable),
            }, null);
        }

        // While game mode is on, or left on, the power plan and visual effects are its own, so what differs from the
        // record is partly its doing. And putting the record back now would not last: turning game mode off puts back
        // the settings it started from — Performance Mode's — with the record gone, and nothing left to undo them.
        // When whether it was left on could not be read, it may have been.
        var on = _gaming.IsActive;
        var leftOn = _gaming.HasPendingRecovery;
        if (on || leftOn is not false)
        {
            return (new Found(UndoChangeKind.PerformanceMode, null, WaitsForGameMode: true,
                GameModeNotKnown: !on && leftOn is null), null);
        }

        IReadOnlyList<PerformanceDifference> differences;
        GraphicsNow? graphics;
        try
        {
            var now = await _readPerformanceNow(ct).ConfigureAwait(false);
            graphics = GraphicsFor(original);
            differences = PerformanceDifferences(original, now, graphics);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or SecurityException
                                       or UnauthorizedAccessException)
        {
            // The record was read; what it would be compared with was not.
            Log.Debug(ex, "Undo Changes could not read the current Performance Mode settings");
            return (Problem(UndoChangeKind.PerformanceMode, UndoProblemKind.CannotCompare), null);
        }

        return (new Found(UndoChangeKind.PerformanceMode,
            differences.Count == 0 ? null : PerformanceChange(original, differences, graphics)), original);
    }

    /// <summary>The graphics setting on the adapter a Performance Mode record names, as it is now.</summary>
    /// <param name="Adapter">What has become of the adapter.</param>
    /// <param name="DynamicPstate">Its setting now — true for adaptive — while it is still there.</param>
    internal readonly record struct GraphicsNow(PerformanceService.RecordedAdapter Adapter, bool DynamicPstate);

    // The real read: the adapter the record names, not the first NVIDIA one found now, since that is the one the
    // restore writes to.
    private static GraphicsNow ReadGraphics(PerformanceService performance, string subKey) =>
        performance.FindAdapter(subKey) is var adapter && adapter == PerformanceService.RecordedAdapter.Present
            ? new GraphicsNow(adapter, !PerformanceService.ReadGpuMaxPerformance(subKey))
            : new GraphicsNow(adapter, DynamicPstate: false);

    private GraphicsNow? GraphicsFor(PerformanceService.OriginalSnapshot original) =>
        original.NvidiaSubKey is { } subKey ? _readGraphics(subKey) : null;

    /// <summary>One setting that differs from Performance Mode's record: what it is called, how it is now, and what putting it back sets.</summary>
    /// <param name="Label">The setting's name, as it starts its line.</param>
    /// <param name="Phrase">The setting's name in a sentence.</param>
    /// <param name="Now">How it is now.</param>
    /// <param name="Before">How it was, which is what putting it back sets.</param>
    internal sealed record PerformanceDifference(string Label, string Phrase, string Now, string Before)
    {
        /// <summary>The line the question lists.</summary>
        public string Line => $"{Label}: {Now} → {Before}";
    }

    /// <summary>
    /// The settings that differ from Performance Mode's record of the original. Pure, so every wording can be pinned.
    /// </summary>
    /// <remarks>
    /// Only what the restore will actually set back is listed: a plan or a processor minimum the record could not read
    /// is left alone by the restore, so it is left out here too, and so is the graphics setting of an adapter that is
    /// no longer in the PC. A value that cannot be read now is listed rather than assumed equal, because the restore
    /// writes it whichever way it is.
    /// </remarks>
    /// <param name="original">The record.</param>
    /// <param name="now">The settings as they are now.</param>
    /// <param name="graphics">
    /// The graphics setting on the adapter the record names, read from that adapter — the one the restore writes to —
    /// rather than taken from <paramref name="now"/>, whose card is the first NVIDIA one found; null when the record
    /// names none.
    /// </param>
    internal static IReadOnlyList<PerformanceDifference> PerformanceDifferences(
        PerformanceService.OriginalSnapshot original, PerformanceService.OriginalSnapshot now, GraphicsNow? graphics)
    {
        List<PerformanceDifference> found = [];

        if (original.PowerPlanGuid.Length > 0
            && !string.Equals(original.PowerPlanGuid, now.PowerPlanGuid, StringComparison.OrdinalIgnoreCase))
        {
            found.Add(new("Power plan", "the power plan",
                now.PowerPlanGuid.Length > 0 ? now.PowerPlanName : "could not be read", original.PowerPlanName));
        }

        if (original.UiEffectsEnabled != now.UiEffectsEnabled)
            found.Add(new("Visual effects", "visual effects", Effects(now.UiEffectsEnabled), Effects(original.UiEffectsEnabled)));

        if (original.GameModeEnabled != now.GameModeEnabled)
            found.Add(new("Game Mode", "Game Mode", OnOff(now.GameModeEnabled), OnOff(original.GameModeEnabled)));

        if (original.XboxGameBarEnabled != now.XboxGameBarEnabled)
            found.Add(new("Xbox Game Bar", "the Xbox Game Bar", OnOff(now.XboxGameBarEnabled), OnOff(original.XboxGameBarEnabled)));

        if (original.XboxGameDvrEnabled != now.XboxGameDvrEnabled)
            found.Add(new("Game DVR", "Game DVR", OnOff(now.XboxGameDvrEnabled), OnOff(original.XboxGameDvrEnabled)));

        if (original.NvidiaSubKey is not null && graphics is { } card)
        {
            var current = card.Adapter switch
            {
                PerformanceService.RecordedAdapter.Unreadable => "could not be read",
                PerformanceService.RecordedAdapter.Present when card.DynamicPstate != original.GpuDynamicPstate
                    => Graphics(card.DynamicPstate),
                _ => null,
            };
            if (current is not null)
                found.Add(new(GraphicsLabel, "the NVIDIA graphics setting", current,
                    $"{Graphics(original.GpuDynamicPstate)}, after a restart"));
        }

        if (original.ProcessorMinPercentAc is int before && now.ProcessorMinPercentAc != before)
        {
            found.Add(new("Processor minimum", "the processor minimum",
                now.ProcessorMinPercentAc is int current ? $"{current}%" : "could not be read", $"{before}%"));
        }

        return found;

        static string Effects(bool enabled) => enabled ? "normal" : "reduced";
        static string OnOff(bool on) => on ? "on" : "off";
        static string Graphics(bool dynamicPstate) => dynamicPstate ? "adaptive" : "maximum performance";
    }

    private UndoChange PerformanceChange(
        PerformanceService.OriginalSnapshot original, IReadOnlyList<PerformanceDifference> differences, GraphicsNow? graphics)
    {
        var when = original.CapturedAtUtc is { } at ? Format(at.UtcDateTime) : null;
        var lines = differences.Select(d => d.Line).ToList();
        // "Changed since", not "changed by": the record holds every setting from before Performance Mode was first
        // used, and one changed later in Windows Settings differs from it just the same.
        return new UndoChange(
            UndoChangeKind.PerformanceMode,
            PerformanceTitle,
            $"Changed since Performance Mode was first used: {JoinAnd(differences.Select(d => d.Phrase).ToList())}.",
            when is null ? "" : $"First changed {when}",
            // The restore writes the graphics card's setting whenever the card the record names may still be there, and
            // only an administrator can write it, so such a record needs administrator rights even when that setting
            // never changed. A card that is gone is left out, and asks for nothing.
            NeedsAdmin: graphics is { Adapter: not PerformanceService.RecordedAdapter.Gone },
            "Put back…",
            lines,
            "Put back Performance Mode?\n\nThese go back to how they were before Performance Mode was first used"
                + (when is null ? ":" : $", on {when}:")
                + $"\n\n{Bullets(lines)}\n\nYou can change any of them again in Performance Mode.",
            "Put back Performance Mode — Confirm");
    }

    private async Task<UndoOutcome> PutBackPerformanceAsync(UndoChange listed, CancellationToken ct)
    {
        var (found, record) = await FindPerformanceAsync(ct).ConfigureAwait(false);
        if (Refusal(listed, found) is { } refusal) return refusal;
        var now = found.Change!;
        var original = record!;

        try
        {
            var cleared = await _restorePerformance(original, ct).ConfigureAwait(false);
            if (!cleared)
                return new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                    "Your original settings are back, but SysManager could not delete its record of them. Restore All "
                    + "on Performance Mode deletes it.");

            var restart = now.Lines.Any(l => l.StartsWith(GraphicsLabel + ":", StringComparison.Ordinal));
            return new UndoOutcome(UndoOutcomeKind.PutBack, restart
                ? "Performance Mode is put back. Restart the PC for the graphics setting to take effect."
                : "Performance Mode is put back: your original settings are on again.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or SecurityException
                                       or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Undo Changes could not put Performance Mode back");

            // Read after the restore rather than assumed: it stops at the first setting it cannot write, so the ones
            // before it may be back already, and the record stays for whatever is not.
            return WhatWentBack(now.Lines, await LinesLeftAsync(original, ct).ConfigureAwait(false)) switch
            {
                StoppedRestore.SomeBack => new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                    $"Performance Mode is only partly back: {ex.Message} The list shows what is still different."),
                StoppedRestore.AllListedBack => new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                    $"Performance Mode's settings are back, but the restore stopped before it finished: {ex.Message} "
                    + "Restore All on Performance Mode tries the rest again."),
                StoppedRestore.NotKnown => new UndoOutcome(UndoOutcomeKind.Failed,
                    $"Performance Mode may be only partly back: {ex.Message} Look again in a moment to see what is still "
                    + "different."),
                _ => new UndoOutcome(UndoOutcomeKind.Failed, $"Performance Mode could not be put back: {ex.Message}"),
            };
        }
        finally
        {
            _signal.Raise(UndoChangeKind.PerformanceMode);
        }
    }

    // The lines that still differ from the record, or null when the settings cannot be read.
    private async Task<IReadOnlyList<string>?> LinesLeftAsync(PerformanceService.OriginalSnapshot original, CancellationToken ct)
    {
        try
        {
            var now = await _readPerformanceNow(ct).ConfigureAwait(false);
            return PerformanceDifferences(original, now, GraphicsFor(original)).Select(d => d.Line).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or SecurityException
                                       or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Undo Changes could not read the Performance Mode settings after a put-back that failed");
            return null;
        }
    }

    /// <summary>What a restore that stopped part-way put back.</summary>
    internal enum StoppedRestore
    {
        /// <summary>None of what the question listed.</summary>
        NothingBack,

        /// <summary>Some of it: the rest still differs.</summary>
        SomeBack,

        /// <summary>All of it, so the step that failed was one the list does not show.</summary>
        AllListedBack,

        /// <summary>What is there now could not be read.</summary>
        NotKnown,
    }

    /// <summary>
    /// What a restore that stopped part-way put back, from the lines its question listed and the ones that still differ
    /// when read again — null when they could not be. Pure, so every case can be pinned.
    /// </summary>
    /// <remarks>
    /// Lines rather than counts. The restore switches the power plan first, and the processor minimum compared after
    /// it is the new plan's, so a restore that switched the plan and then failed can leave as many lines as it started
    /// with — different ones. A count said nothing had changed; the lines say the plan went back.
    /// </remarks>
    internal static StoppedRestore WhatWentBack(IReadOnlyList<string> listed, IReadOnlyList<string>? left) => left switch
    {
        null => StoppedRestore.NotKnown,
        { Count: 0 } => StoppedRestore.AllListedBack,
        _ when listed.Any(line => !left.Contains(line, StringComparer.Ordinal)) => StoppedRestore.SomeBack,
        _ => StoppedRestore.NothingBack,
    };

    // ── Services ───────────────────────────────────────────────────────────────

    // A service SysManager turned off that is still off, with the name Windows shows for it.
    private readonly record struct TurnedOff(ServiceStartupRecord Record, string Name);

    private (Found Found, IReadOnlyList<TurnedOff> Off) FindServices()
    {
        var ledger = _ledger.Load();
        if (ledger is null) return (Problem(UndoChangeKind.Services, UndoProblemKind.Unreadable), []);

        List<TurnedOff> off = [];
        foreach (var record in ledger.Values)
        {
            // Disable refuses a name it would not pass to sc.exe, so a record carrying one did not come from here.
            if (!ServiceManagerService.IsSafeForScExe(record.ServiceName)) continue;

            // Only while it is still off: one turned back on outside SysManager has nothing left to put back, and
            // the Services tab ignores its record for the same reason.
            if (_readService(record.ServiceName) is not { } entry || !ServiceManagerService.IsDisabled(entry)) continue;

            off.Add(new TurnedOff(record, entry.DisplayName.Length > 0 ? entry.DisplayName : record.ServiceName));
        }

        if (off.Count == 0) return (new Found(UndoChangeKind.Services, null), off);

        off.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return (new Found(UndoChangeKind.Services, ServicesChange(off)), off);
    }

    private UndoChange ServicesChange(IReadOnlyList<TurnedOff> off)
    {
        var lines = off.Select(o => $"{o.Name}: Disabled → {o.Record.PreviousStartType}").ToList();
        var names = off.Select(o => o.Name).ToList();
        var newest = Format(off.Max(o => o.Record.DisabledAtUtc).UtcDateTime);
        var one = off.Count == 1;

        return new UndoChange(
            UndoChangeKind.Services,
            ServicesTitle,
            one
                ? $"Turned off {names[0]}. It goes back to the startup type it had."
                : off.Count <= 3
                    ? $"Turned off {off.Count} services: {JoinAnd(names)}. Each goes back to the startup type it had."
                    : $"Turned off {off.Count} services, among them {JoinAnd(names.Take(3).ToList())}. Each goes back "
                      + "to the startup type it had.",
            one ? $"Turned off {newest}" : $"The last one turned off {newest}",
            NeedsAdmin: true,
            "Turn back on…",
            lines,
            $"Turn {(one ? "this service" : "these services")} back on?\n\n{Bullets(lines)}\n\n"
                + (one
                    ? "It goes back to the startup type it had before SysManager turned it off. If that is automatic, it "
                      + "starts again at the next restart; if it is Manual, when something needs it."
                    : "Each goes back to the startup type it had before SysManager turned it off. One that starts "
                      + "automatically starts again at the next restart, and one set to Manual when something needs it."),
            "Turn services back on — Confirm");
    }

    private async Task<UndoOutcome> PutBackServicesAsync(UndoChange listed, CancellationToken ct)
    {
        var (found, off) = FindServices();
        if (Refusal(listed, found) is { } refusal) return refusal;

        List<string> refused = [];
        var done = 0;
        try
        {
            foreach (var service in off)
            {
                try
                {
                    await ServiceManagerService.PutBackStartupTypeAsync(
                        service.Record.ServiceName, service.Record.PreviousStartType, _powerShell, _ledger, ct)
                        .ConfigureAwait(false);
                    done++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
                {
                    Log.Warning(ex, "Undo Changes could not turn {Service} back on", service.Record.ServiceName);
                    refused.Add(service.Name);
                }
            }
        }
        finally
        {
            // Not when Windows refused every one: those are as they were, and the Services tab would read every service
            // on the PC again for nothing.
            if (refused.Count < off.Count) _signal.Raise(UndoChangeKind.Services);
        }

        if (refused.Count == 0)
            return new UndoOutcome(UndoOutcomeKind.PutBack, done == 1
                ? $"{off[0].Name} is back on: it starts the way it did before SysManager turned it off."
                : $"{done} services are back on: each starts the way it did before SysManager turned it off.");

        return done == 0
            ? new UndoOutcome(UndoOutcomeKind.Failed,
                $"Windows would not turn {JoinAnd(refused)} back on. The log has the reason.")
            : new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                $"{done} of {off.Count} services {(done == 1 ? "is" : "are")} back on. Windows would not turn "
                + $"{JoinAnd(refused)} back on; the log has the reason.");
    }

    // ── Hosts file ─────────────────────────────────────────────────────────────

    private Found FindHosts()
    {
        HostsBackupState? state;
        try
        {
            state = _hosts.ReadBackupState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Either file may be the one that could not be read, and either way the two were not compared.
            Log.Debug(ex, "Undo Changes could not compare the hosts file with the copy beside it");
            return Problem(UndoChangeKind.HostsFile, UndoProblemKind.CannotCompare);
        }

        // Only a hosts file SysManager has written. A copy beside one it never touched is not a change of its own,
        // whoever made the copy — another program, or the user, years ago.
        if (state is not { DiffersFromHosts: true, WrittenBySysManager: true }) return new Found(UndoChangeKind.HostsFile, null);

        var taken = Format(state.TakenAtUtc);
        return new Found(UndoChangeKind.HostsFile, new UndoChange(
            UndoChangeKind.HostsFile,
            HostsTitle,
            "SysManager has changed the hosts file since the copy beside it was made.",
            $"Copy from {taken}",
            NeedsAdmin: true,
            "Restore the copy…",
            [],
            $"Restore the hosts file from the copy beside it, made on {taken}?\n\nEverything written to it since — by "
                + "SysManager or by any other program — is replaced by that copy.",
            "Restore the hosts file — Confirm"));
    }

    private UndoOutcome PutBackHosts()
    {
        // The restore swaps a finished copy into place whole (AtomicFile), so one that failed left the file as it was,
        // and only one that worked is told to DNS & Hosts — which would otherwise read the file again, and drop the
        // entries being edited there, for nothing.
        bool restored;
        try
        {
            restored = _hosts.RestoreBackup();
        }
        catch (UnauthorizedAccessException ex)
        {
            // Only ever as administrator, so not a matter of rights: the file is read-only, or something guards it.
            Log.Warning(ex, "Undo Changes could not restore the hosts file");
            return new UndoOutcome(UndoOutcomeKind.Failed,
                "Windows would not let SysManager replace the hosts file, so nothing was changed. The file may be set to "
                + "read-only, or protected by security software.");
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Undo Changes could not restore the hosts file");
            return new UndoOutcome(UndoOutcomeKind.Failed, $"The hosts file could not be restored, so nothing was changed: {ex.Message}");
        }

        if (!restored) return new UndoOutcome(UndoOutcomeKind.NothingLeft, "There is no copy of the hosts file to restore.");

        _signal.Raise(UndoChangeKind.HostsFile);
        return new UndoOutcome(UndoOutcomeKind.PutBack, "The hosts file is back to the copy beside it.");
    }

    // ── Environment variables ──────────────────────────────────────────────────

    private (Found Found, EnvironmentVariableService.RestorePreview? Preview) FindEnvironment()
    {
        EnvironmentVariableService.RestorePreview? preview;
        try
        {
            preview = _environment.PreviewRestore();
        }
        catch (InvalidDataException ex)
        {
            Log.Debug(ex, "Undo Changes found the kept copy of the environment variables damaged");
            return (Problem(UndoChangeKind.EnvironmentVariables, UndoProblemKind.Damaged), null);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            // The copy or the variables themselves: either way, the two were not compared.
            Log.Debug(ex, "Undo Changes could not compare the environment variables with their kept copy");
            return (Problem(UndoChangeKind.EnvironmentVariables, UndoProblemKind.CannotCompare), null);
        }

        if (preview is null || preview.Differences.Count == 0)
            return (new Found(UndoChangeKind.EnvironmentVariables, null), preview);

        var lines = preview.Differences.Select(EnvironmentLine).ToList();
        var count = preview.Differences.Count;
        return (new Found(UndoChangeKind.EnvironmentVariables, new UndoChange(
            UndoChangeKind.EnvironmentVariables,
            EnvironmentTitle,
            $"A copy of your variables from before SysManager first changed them. {count} "
                + $"{(count == 1 ? "differs" : "differ")} from it now.",
            (preview.CoversUser, preview.CoversMachine) switch
            {
                (true, true) => "Your variables and the machine-wide ones are restored together",
                (false, true) => "Only the machine-wide variables were copied",
                _ => "Only your own variables were copied",
            },
            // The restore writes every machine-wide variable the copy holds, and only an administrator can.
            NeedsAdmin: preview.CoversMachine,
            "Restore the copy…",
            lines,
            $"Put your environment variables back to the copy SysManager kept?\n\n{Bullets(lines)}\n\nEvery variable "
                + "goes back to that copy, including changes other programs made since, and the ones added since are "
                + "removed. Programs that are already open may need a restart to see the change.",
            "Restore environment variables — Confirm")), preview);
    }

    /// <summary>The line the question lists for one variable. Pure, so every wording can be pinned.</summary>
    /// <remarks>Names, not values: a PATH runs to hundreds of characters, and the question has to fit on a screen.</remarks>
    internal static string EnvironmentLine(EnvironmentVariableService.Difference difference)
    {
        var whose = difference.Scope == EnvVarScope.Machine ? "machine-wide" : "yours";
        return difference.Kind switch
        {
            EnvironmentVariableService.DifferenceKind.AddedBack => $"{difference.Name} ({whose}): put back",
            EnvironmentVariableService.DifferenceKind.Removed => $"{difference.Name} ({whose}): removed, added since",
            _ => $"{difference.Name} ({whose}): back to its earlier value",
        };
    }

    private UndoOutcome PutBackEnvironment(UndoChange listed)
    {
        var (found, preview) = FindEnvironment();
        if (Refusal(listed, found) is { } refusal) return refusal;

        var differed = preview!.Differences.Count;
        // Told to the Environment Variables tab unless the restore refused before writing anything: it would read the
        // variables again, and drop what is being edited there, for nothing.
        var mayHaveWritten = true;
        try
        {
            EnvironmentVariableService.RestoreResult result;
            try
            {
                result = _restoreEnvironment();
            }
            catch (IOException ex)
            {
                // Variables before the one that threw may be written already, so what is left is read, not assumed.
                Log.Warning(ex, "Undo Changes could not restore the environment variables");
                var stopped = WhatWentBack(listed.Lines, LinesLeft());
                if (stopped != StoppedRestore.NothingBack) _announceEnvironment();
                return stopped switch
                {
                    StoppedRestore.SomeBack => new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                        $"The variables are only partly back: {ex.Message} The list shows what is still different."),
                    StoppedRestore.AllListedBack => new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                        $"The variables are back to the copy SysManager kept, but Windows reported a problem on the way: {ex.Message}"),
                    StoppedRestore.NotKnown => new UndoOutcome(UndoOutcomeKind.Failed,
                        $"The variables may be only partly back: {ex.Message} Look again in a moment to see what is still "
                        + "different."),
                    _ => new UndoOutcome(UndoOutcomeKind.Failed, $"The variables could not be restored: {ex.Message}"),
                };
            }

            // Both are found before the first write.
            if (result.InvalidBackup)
            {
                mayHaveWritten = false;
                return new UndoOutcome(UndoOutcomeKind.Failed,
                    "What SysManager kept for the environment variables is damaged, so nothing was changed.");
            }
            if (!result.HadBackup)
            {
                mayHaveWritten = false;
                return new UndoOutcome(UndoOutcomeKind.NothingLeft, "There is no copy of the environment variables to restore.");
            }

            // What is left, read after the restore rather than worked out from its own tally, which counts every variable
            // it wrote — the unchanged ones included — and every one it refused, the ones no line listed included.
            var left = LinesLeft()?.Count;
            var changed = left is { } l ? l < differed : result.Restored > 0 || result.Removed > 0;
            // Once, after the batch, as the tab does — the broadcast is what tells Explorer and new programs.
            if (changed) _announceEnvironment();

            var lead = $"{differed} {(differed == 1 ? "variable is" : "variables are")} back to the copy SysManager kept";
            return left switch
            {
                0 => new UndoOutcome(UndoOutcomeKind.PutBack, $"{lead}."),
                { } remaining when remaining < differed => new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                    $"{differed - remaining} of {differed} variables {(differed - remaining == 1 ? "is" : "are")} back to "
                    + $"the copy SysManager kept; {remaining} could not be written. The log has the reason."),
                { } => new UndoOutcome(UndoOutcomeKind.Failed, "Windows would not write the variables back. The log has the reason."),
                null => result.Failed == 0
                    ? new UndoOutcome(UndoOutcomeKind.PutBack, $"{lead}.")
                    : new UndoOutcome(UndoOutcomeKind.PartlyPutBack,
                        $"The variables are partly back: {result.Failed} could not be written. The log has the reason."),
            };
        }
        finally
        {
            if (mayHaveWritten) _signal.Raise(UndoChangeKind.EnvironmentVariables);
        }
    }

    // The lines for the variables that still differ from the copy, or null when the copy or the variables cannot be
    // read now. The restore never makes a variable differ that did not, so in the run that finished, their count is
    // enough; the one that stopped compares the lines themselves (WhatWentBack).
    private IReadOnlyList<string>? LinesLeft()
    {
        try
        {
            return _environment.PreviewRestore()?.Differences.Select(EnvironmentLine).ToList() ?? [];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SecurityException
                                       or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Undo Changes could not compare the environment variables with their copy after restoring it");
            return null;
        }
    }

    // ── Gaming Profile ─────────────────────────────────────────────────────────

    private Found FindGaming()
    {
        if (_gaming.IsActive)
        {
            return new Found(UndoChangeKind.GamingProfile, new UndoChange(
                UndoChangeKind.GamingProfile,
                GamingTitle,
                "Game mode is on.",
                "On now",
                NeedsAdmin: false,
                "Turn it off…",
                [],
                "Turn game mode off?\n\nEverything it changed goes back to how it was before it started.",
                "Turn game mode off — Confirm"));
        }

        var leftOn = _gaming.HasPendingRecovery;
        if (leftOn is null) return Problem(UndoChangeKind.GamingProfile, UndoProblemKind.Unreadable);
        if (leftOn == false) return new Found(UndoChangeKind.GamingProfile, null);

        return new Found(UndoChangeKind.GamingProfile, new UndoChange(
            UndoChangeKind.GamingProfile,
            GamingTitle,
            "Still on from a session that did not end cleanly: SysManager closed while game mode was on.",
            "Left on by an earlier run",
            NeedsAdmin: false,
            "Turn it off…",
            [],
            "SysManager closed while game mode was still on.\n\nPut back what it changed (power plan, visual effects, "
                + "search indexing, notifications)?",
            "Turn game mode off — Confirm"));
    }

    private async Task<UndoOutcome> PutBackGamingAsync(CancellationToken ct)
    {
        // No lock taken here: both reverts take the system-modification lock themselves when it is free, and never
        // refuse, because a refusal would leave a game's changes on with nothing left to undo them.
        GamingRevertResult result;
        try
        {
            result = _gaming.IsActive
                ? await _gaming.RevertAsync(ct).ConfigureAwait(false)
                : await _gaming.RecoverPendingAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _signal.Raise(UndoChangeKind.GamingProfile);
        }

        return result.FullyRestored
            ? new UndoOutcome(UndoOutcomeKind.PutBack, "Game mode is off, and what it changed is back.")
            : new UndoOutcome(UndoOutcomeKind.PartlyPutBack, result.Describe("", "Game mode is off"));
    }

    // ── Settings Watchdog ──────────────────────────────────────────────────────

    private Found FindWatchdog()
    {
        if (_watchdog.LoadBaseline() is not { } baseline) return new Found(UndoChangeKind.SettingsWatchdog, null);

        // Only the ones Settings Watchdog can write back. The rest it shows for awareness, and listing them here would
        // send someone to a tab that cannot put them back either.
        var drifted = _watchdog.DetectDrift(baseline, _watchdog.ReadCurrent()).Where(d => d.CanRestore).ToList();
        if (drifted.Count == 0) return new Found(UndoChangeKind.SettingsWatchdog, null);

        return new Found(UndoChangeKind.SettingsWatchdog, new UndoChange(
            UndoChangeKind.SettingsWatchdog,
            WatchdogTitle,
            drifted.Count == 1
                ? $"{drifted[0].Setting.Name} no longer matches the setting you saved in Settings Watchdog."
                : $"{drifted.Count} settings no longer match the ones you saved in Settings Watchdog, for example "
                  + $"{drifted[0].Setting.Name}.",
            "Checked just now",
            NeedsAdmin: false,
            "Review in Settings Watchdog",
            drifted.Select(d => d.Summary).ToList(),
            "",
            "",
            OpensTab: WatchdogTab));
    }

    // ── Wording ────────────────────────────────────────────────────────────────

    private static UndoOutcome Busy() =>
        new(UndoOutcomeKind.Busy,
            $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification) ?? "another change"} "
            + "is already running.");

    /// <summary>A date the way the rows write it.</summary>
    private string Format(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone)
            .ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"a", "a and b", "a, b and c".</summary>
    internal static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    /// <summary>The lines as a list, cut at <see cref="MaxQuestionLines"/> with a count of the rest.</summary>
    internal static string Bullets(IReadOnlyList<string> lines)
    {
        if (lines.Count <= MaxQuestionLines)
            return string.Join("\n", lines.Select(l => $"• {l}"));

        var shown = lines.Take(MaxQuestionLines - 1).Select(l => $"• {l}");
        return string.Join("\n", shown) + $"\n• and {lines.Count - (MaxQuestionLines - 1)} more";
    }
}
