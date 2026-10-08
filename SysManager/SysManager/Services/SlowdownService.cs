// SysManager · SlowdownService — what is slowing this PC down right now
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>The Dashboard's "Why is it slow?" check (#1529).</summary>
public interface ISlowdownService
{
    /// <summary>
    /// Looks at the five things in <see cref="SlowdownProbe"/> at once and returns what is likely slowing the PC down,
    /// worst first. Reports each of the five as it is done with it.
    /// </summary>
    Task<SlowdownReport> CheckAsync(IProgress<SlowdownProbeStatus>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Answers "why is my PC slow right now?" from what other tabs already read: the system drive's free space, scored as the
/// Health Score scores it; the program using the most of the processor; what starts with Windows, and what Windows blamed
/// for a slow start; how much memory is in use; and how long Windows has run since its last restart.
/// </summary>
/// <remarks>
/// Read-only. Nothing here changes anything on the PC, and a finding only names the tab that can act on it.
/// <para>Each finding is an observation, what it means in plain words, then the tab to open: the tone of the network
/// verdicts in <see cref="HealthAnalyzer"/>. A program using the processor is named, and in the same sentence allowed to
/// be exactly what the user wants, because the check cannot tell a video being exported on purpose from a program
/// running away.</para>
/// <para>Where the Health Score already has a line, this uses it rather than a copy: the drive is judged by
/// <see cref="HealthScoreService.ComputeFreeSpaceScore"/> and the restart by
/// <see cref="HealthScoreService.ComputeUptimeScore"/>, so the two cards on the Dashboard cannot disagree.</para>
/// </remarks>
public sealed class SlowdownService : ISlowdownService
{
    /// <summary>The share of the processor one program has to be using in both samples to be named.</summary>
    internal const double ProcessorBusyPercent = 40;

    /// <summary>The share of the memory in use at which the check says so.</summary>
    /// <remarks>
    /// Higher than the 80% at which the Health Score recommends closing apps, because this answers what is slowing the
    /// PC down now, and that starts when Windows has to move memory to the disk to make room.
    /// </remarks>
    internal const double MemoryFullPercent = 90;

    /// <summary>More programs than this starting with Windows is worth a look.</summary>
    internal const int StartupProgramsAbove = 10;

    /// <summary>More delay than this, blamed by Windows on what ran during its last timed start, is worth a look.</summary>
    internal static readonly TimeSpan SlowStartAbove = TimeSpan.FromSeconds(20);

    /// <summary>How recent the last start Windows timed has to be for what it blamed then to count.</summary>
    /// <remarks>
    /// Windows does not time every start, so the last one it timed can be weeks old, and a program it blamed then may
    /// have been removed since. Past this, the check goes by the startup programs alone.
    /// </remarks>
    internal static readonly TimeSpan BlameKeptFor = TimeSpan.FromDays(30);

    /// <summary>How far apart the two processor samples are, so a program busy for a moment is not named.</summary>
    internal static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far apart the record of a start and the records of what slowed it can be. Windows writes them in one pass,
    /// with the same second on each; the margin is for a log written while the PC is busy.
    /// </summary>
    private static readonly TimeSpan SameStart = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlySet<int>?, CancellationToken, Task<IReadOnlyList<ProcessEntry>>> _processes;
    private readonly Func<IReadOnlyList<FixedDriveService.FixedDrive>> _drives;
    private readonly Func<string?> _systemDrive;
    private readonly Func<CancellationToken, Task<SystemSnapshot>> _vitals;
    private readonly Func<CancellationToken, Task<StartupScan>> _startup;
    private readonly Func<CancellationToken, Task<BootRecord?>> _lastTimedStart;
    private readonly Func<CancellationToken, Task<IReadOnlyList<BootDegradation>?>> _blamed;
    private readonly Func<string, ProcessDescriptionEntry?> _describe;
    private readonly int _ownProcessId;
    private readonly TimeProvider _clock;

    /// <summary>The check production uses, over the services of the tabs that own each of the five.</summary>
    public SlowdownService(ProcessManagerService processes, SystemInfoService sysInfo, StartupService startup,
                           BootAnalyzerService boot)
        : this((processes ?? throw new ArgumentNullException(nameof(processes))).SnapshotAsync,
               FixedDriveService.Enumerate,
               HealthScoreService.SystemDriveLetter,
               (sysInfo ?? throw new ArgumentNullException(nameof(sysInfo))).CaptureAsync,
               (startup ?? throw new ArgumentNullException(nameof(startup))).ScanAsync,
               LastTimedStart(boot ?? throw new ArgumentNullException(nameof(boot))),
               Blamed(boot),
               // Deferred, so building the service does not load the database before anything asks it a question.
               name => ProcessDescriptionService.Instance.Lookup(name),
               Environment.ProcessId,
               TimeProvider.System)
    {
    }

    /// <summary>A check over fixed sources and a fixed clock, for a test.</summary>
    /// <param name="processes">Lists the running processes, skipping what it already read about the ones it is given.</param>
    /// <param name="drives">The fixed drives.</param>
    /// <param name="systemDrive">The letter of the drive Windows is installed on, or null when it cannot be told.</param>
    /// <param name="vitals">Memory in use and the time since boot.</param>
    /// <param name="startup">What starts with Windows.</param>
    /// <param name="lastTimedStart">The newest start Windows timed, or null when its log could not be read or holds none.</param>
    /// <param name="blamed">What Windows blamed for slow starts, newest first, or null when its log could not be read.</param>
    /// <param name="describe">SysManager's own record of a program, by process name, or null when it has none.</param>
    /// <param name="ownProcessId">SysManager's own process, which is doing the measuring and is never named.</param>
    /// <param name="clock">The time now, and the wait between the two processor samples.</param>
    internal SlowdownService(
        Func<IReadOnlySet<int>?, CancellationToken, Task<IReadOnlyList<ProcessEntry>>> processes,
        Func<IReadOnlyList<FixedDriveService.FixedDrive>> drives,
        Func<string?> systemDrive,
        Func<CancellationToken, Task<SystemSnapshot>> vitals,
        Func<CancellationToken, Task<StartupScan>> startup,
        Func<CancellationToken, Task<BootRecord?>> lastTimedStart,
        Func<CancellationToken, Task<IReadOnlyList<BootDegradation>?>> blamed,
        Func<string, ProcessDescriptionEntry?> describe,
        int ownProcessId,
        TimeProvider clock)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _systemDrive = systemDrive ?? throw new ArgumentNullException(nameof(systemDrive));
        _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _lastTimedStart = lastTimedStart ?? throw new ArgumentNullException(nameof(lastTimedStart));
        _blamed = blamed ?? throw new ArgumentNullException(nameof(blamed));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _ownProcessId = ownProcessId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    private static Func<CancellationToken, Task<BootRecord?>> LastTimedStart(BootAnalyzerService boot)
        => async ct => (await boot.ReadBootsAsync(1, ct).ConfigureAwait(false))?.FirstOrDefault();

    private static Func<CancellationToken, Task<IReadOnlyList<BootDegradation>?>> Blamed(BootAnalyzerService boot)
        => ct => boot.ReadDegradationsAsync(ct: ct);

    public async Task<SlowdownReport> CheckAsync(IProgress<SlowdownProbeStatus>? progress = null, CancellationToken ct = default)
    {
        // All at once: the processor samples alone take over a second, and nothing else has to wait for them.
        var drive = DiskSpaceAsync(progress, ct);
        var processor = ProcessorAsync(progress, ct);
        var startup = StartupAsync(progress, ct);
        var vitals = VitalsAsync(progress, ct);

        // Every one has finished before any is read, so a cancellation or a fault in one cannot leave another running
        // with nobody waiting for it.
        await Task.WhenAll(drive, processor, startup, vitals).ConfigureAwait(false);

        var (memory, uptime) = await vitals.ConfigureAwait(false);
        ProbeOutcome[] outcomes =
        [
            await drive.ConfigureAwait(false),
            await processor.ConfigureAwait(false),
            await startup.ConfigureAwait(false),
            memory,
            uptime,
        ];

        return new SlowdownReport(
            Rank(outcomes.Select(o => o.Finding)),
            [.. outcomes.Where(o => !o.Status.Read).Select(o => o.Status.Probe)],
            _clock.GetLocalNow().DateTime);
    }

    // ── The five ───────────────────────────────────────────────────────

    private async Task<ProbeOutcome> DiskSpaceAsync(IProgress<SlowdownProbeStatus>? progress, CancellationToken ct)
    {
        IReadOnlyList<FixedDriveService.FixedDrive>? drives = null;
        string? systemDrive = null;
        try
        {
            (drives, systemDrive) = await Task.Run(() => (_drives(), _systemDrive()), ct).ConfigureAwait(false);
        }
        // The list of drives the enumeration starts from is read outside its guard for each drive.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Slowdown check: the drives could not be read: {Error}", ex.Message);
        }

        var outcome = DiskSpace(drives, systemDrive);
        progress?.Report(outcome.Status);
        return outcome;
    }

    private async Task<ProbeOutcome> ProcessorAsync(IProgress<SlowdownProbeStatus>? progress, CancellationToken ct)
    {
        // Twice, a second apart. One sample cannot tell a program that is busy from one that was busy for a moment — the
        // moment the button was pressed, for one — so a program is named for what it used in both.
        var first = await _processes(null, ct).ConfigureAwait(false);
        await Task.Delay(SampleGap, _clock, ct).ConfigureAwait(false);

        // The second pass skips what the first already read about each process, as Process Manager's refresh does.
        var second = await _processes(first.Select(p => p.Pid).ToHashSet(), ct).ConfigureAwait(false);

        var outcome = Processor(first, second, _ownProcessId, _describe);
        progress?.Report(outcome.Status);
        return outcome;
    }

    private async Task<ProbeOutcome> StartupAsync(IProgress<SlowdownProbeStatus>? progress, CancellationToken ct)
    {
        var scan = ScanOrNullAsync(ct);
        var lastTimedStart = _lastTimedStart(ct);
        var blamed = _blamed(ct);
        await Task.WhenAll(scan, lastTimedStart, blamed).ConfigureAwait(false);

        var outcome = Startup(await scan.ConfigureAwait(false), await lastTimedStart.ConfigureAwait(false),
                              await blamed.ConfigureAwait(false), _clock.GetLocalNow().DateTime, _describe);
        progress?.Report(outcome.Status);
        return outcome;
    }

    private async Task<StartupScan?> ScanOrNullAsync(CancellationToken ct)
    {
        try
        {
            return await _startup(ct).ConfigureAwait(false);
        }
        // The scan reads the registry and the startup folders. Each read guards itself, and these are what is left.
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warning("Slowdown check: the startup programs could not be read: {Error}", ex.Message);
            return null;
        }
    }

    private async Task<(ProbeOutcome Memory, ProbeOutcome Uptime)> VitalsAsync(IProgress<SlowdownProbeStatus>? progress,
                                                                              CancellationToken ct)
    {
        SystemSnapshot? snapshot = null;
        try
        {
            snapshot = await _vitals(ct).ConfigureAwait(false);
        }
        // The three TuneUpService.CaptureVitalsAsync allows for: WMI raises COMException on repository and RPC failures.
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or COMException)
        {
            Log.Warning("Slowdown check: memory and uptime could not be read: {Error}", ex.Message);
        }

        var memory = Memory(snapshot);
        var uptime = Uptime(snapshot);
        progress?.Report(memory.Status);
        progress?.Report(uptime.Status);
        return (memory, uptime);
    }

    // ── What each one found (pure, so every threshold and every sentence is testable) ──

    /// <summary>What one of the five came to: where it stands, and what it found, if anything.</summary>
    internal sealed record ProbeOutcome(SlowdownProbeStatus Status, SlowdownFinding? Finding)
    {
        /// <summary>It was read, and says <paramref name="summary"/>; <paramref name="finding"/> when it found something.</summary>
        public static ProbeOutcome Of(SlowdownProbe probe, string summary, SlowdownFinding? finding = null) =>
            new(new SlowdownProbeStatus(probe, Done: true, Read: true, summary), finding);

        /// <summary>It could not be read, so it found nothing, which is not the same as there being nothing.</summary>
        public static ProbeOutcome Unread(SlowdownProbe probe) =>
            new(new SlowdownProbeStatus(probe, Done: true, Read: false, Summary: ""), null);
    }

    /// <summary>The system drive, judged as the Health Score judges it.</summary>
    internal static ProbeOutcome DiskSpace(IReadOnlyList<FixedDriveService.FixedDrive>? drives, string? systemDrive)
    {
        // The test HealthScoreService.UnavailableComponents makes: the system drive itself, with a size to divide by.
        var drive = string.IsNullOrWhiteSpace(systemDrive)
            ? null
            : drives?.FirstOrDefault(d => string.Equals(d.Letter, systemDrive, StringComparison.OrdinalIgnoreCase));
        if (drive is null || drive.SizeGB <= 0) return ProbeOutcome.Unread(SlowdownProbe.DiskSpace);

        var used = (drive.SizeGB - drive.FreeGB) / drive.SizeGB * 100;
        var summary = string.Create(CultureInfo.InvariantCulture, $"{drive.Letter} {used:F0}% full");

        // The score, not the percentage alone: 10% of a 4 TB drive is 400 GB, and nothing is wrong with it.
        var score = HealthScoreService.ComputeFreeSpaceScore(drives, systemDrive);
        if (score >= HealthScoreService.FreeSpaceRecommendBelow) return ProbeOutcome.Of(SlowdownProbe.DiskSpace, summary);

        var nearlyFull = score <= HealthScoreService.FreeSpaceCriticalAtOrBelow;
        var left = drive.FreeGB < 1
            ? "Less than 1 GB is"
            : string.Create(CultureInfo.InvariantCulture, $"Only {drive.FreeGB:F0} GB is");
        var detail = string.Create(CultureInfo.InvariantCulture, $"That is {used:F0}% of the drive in use. ")
            + (nearlyFull
                ? "Windows needs room to work, and with this little left everything gets slower, not just saving files. "
                  + "This is the one worth fixing first."
                : "Windows needs room to work and to install its updates, and this is getting tight.");

        return ProbeOutcome.Of(SlowdownProbe.DiskSpace, summary, new SlowdownFinding(
            nearlyFull ? SlowdownKind.DriveNearlyFull : SlowdownKind.DriveFilling,
            $"{left} left on the {drive.Letter} drive",
            detail,
            [
                new("Free up space", "nav-deep-cleanup", "Deep Cleanup", IsPrimary: nearlyFull),
                new("See what is using it", "nav-disk-analyzer", "Disk Analyzer"),
            ]));
    }

    /// <summary>The program using the most of the processor in both samples, by program rather than by process.</summary>
    /// <remarks>
    /// By program because a browser runs as a dozen processes, and none of them alone is the answer. Each process's share
    /// is of the whole processor, every core counted, as Process Manager shows it.
    /// </remarks>
    internal static ProbeOutcome Processor(IReadOnlyList<ProcessEntry> first, IReadOnlyList<ProcessEntry> second,
                                           int ownProcessId, Func<string, ProcessDescriptionEntry?> describe)
    {
        // An empty list is the snapshot's own answer when Windows would not list the processes at all.
        if (first.Count == 0 || second.Count == 0) return ProbeOutcome.Unread(SlowdownProbe.Processor);

        var before = ByProgram(first, ownProcessId);
        var after = ByProgram(second, ownProcessId);
        var busiest = before.Keys
            .Where(after.ContainsKey)
            .Select(program => (Program: program, Percent: Math.Min(before[program], after[program])))
            .OrderByDescending(b => b.Percent)
            .ThenBy(b => b.Program, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (busiest.Program is null || busiest.Percent < ProcessorBusyPercent)
            return ProbeOutcome.Of(SlowdownProbe.Processor, "nothing unusual");

        var name = DisplayName(busiest.Program, describe, first);
        // Capped, because two readings of a whole processor's worth, each rounded, can add up to a hair over all of it.
        var percent = string.Create(CultureInfo.InvariantCulture, $"{Math.Min(busiest.Percent, 100):F0}%");
        var processes = second.Count(p => Counts(p, ownProcessId) && IsProgram(p, busiest.Program));
        var partOfWindows = describe(busiest.Program)?.Safety == ProcessSafety.System;
        var detail = (processes > 1 ? $"Across its {processes} processes, right now. " : "Right now, and not just for a moment. ")
            + (partOfWindows
                ? "It is part of Windows, and that usually means it is busy with a job of its own, such as a scan or an "
                  + "update, that finishes by itself."
                : "That may be exactly what you want if you are watching or playing something. It is only worth acting on "
                  + "if the PC feels slow while you are not.");

        return ProbeOutcome.Of(SlowdownProbe.Processor, $"{name} {percent}", new SlowdownFinding(
            SlowdownKind.ProcessorBusy,
            $"{name} is using {percent} of the processor",
            detail,
            [new("See what is running", "nav-processes", "Process Manager")]));
    }

    private static Dictionary<string, double> ByProgram(IReadOnlyList<ProcessEntry> processes, int ownProcessId)
    {
        var byProgram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes.Where(p => Counts(p, ownProcessId)))
            byProgram[p.Name] = byProgram.GetValueOrDefault(p.Name) + p.CpuPercent;
        return byProgram;
    }

    /// <summary>
    /// Whether a process counts: not the idle process, which is the processor doing nothing, and not SysManager, which is
    /// doing the measuring.
    /// </summary>
    private static bool Counts(ProcessEntry p, int ownProcessId) => p.Pid != 0 && p.Pid != ownProcessId;

    private static bool IsProgram(ProcessEntry p, string program) =>
        string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name a person knows a program by: SysManager's own record of it first ("Google Chrome"), then the name its file
    /// gives itself, then the process name.
    /// </summary>
    internal static string DisplayName(string program, Func<string, ProcessDescriptionEntry?> describe,
                                       IEnumerable<ProcessEntry> seen)
    {
        if (describe(program)?.ShortName is { Length: > 0 } known) return known;

        var fromFile = seen
            .Where(p => IsProgram(p, program))
            .Select(p => p.Description)
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        return fromFile?.Trim() ?? program;
    }

    /// <summary>What starts with Windows, and what Windows blamed for slowing its last timed start.</summary>
    internal static ProbeOutcome Startup(StartupScan? scan, BootRecord? lastTimedStart, IReadOnlyList<BootDegradation>? blamed,
                                         DateTime now, Func<string, ProcessDescriptionEntry?> describe)
    {
        if (scan is null) return ProbeOutcome.Unread(SlowdownProbe.Startup);

        // Counted the way Startup Manager counts "enabled", so the number is the one the tab shows. Without
        // administrator rights Windows keeps other programs' scheduled tasks to itself, so the count is then only what
        // could be seen, and the words say so.
        var enabled = scan.Entries.Count(e => e.IsEnabled);
        var atLeast = !scan.ScheduledTasksListed;
        var summary = $"{(atLeast ? "at least " : "")}{enabled} start with Windows";

        var slowed = BlamedAtLastStart(lastTimedStart, blamed, now);
        var many = enabled > StartupProgramsAbove;
        var slow = slowed.Sum(d => d.DurationMs) > SlowStartAbove.TotalMilliseconds;
        if (!many && !slow) return ProbeOutcome.Of(SlowdownProbe.Startup, summary);

        // Startup Manager can turn a program off. A driver or a device Windows blamed is Boot Analyzer's to explain, and
        // the link each one carries there says where it can be dealt with.
        var toStartupManager = many || slowed[0].NavTargetId == "nav-startup";

        var detail = slowed.Count > 0
            ? $"Windows itself blamed {Blame(slowed, describe)} for slowing the last start it timed, on "
              + $"{slowed[0].When.ToString("d MMMM", CultureInfo.InvariantCulture)}."
            : "Each one adds to how long the PC takes to be ready, and many keep running in the background afterwards.";
        if (toStartupManager) detail += " Turning one off is reversible: it can be switched back on from the same screen.";

        List<SlowdownAction> actions = [];
        if (toStartupManager) actions.Add(new("Review startup programs", "nav-startup", "Startup Manager"));
        if (slowed.Count > 0) actions.Add(new("See what slowed the start", "nav-boot-analyzer", "Boot Analyzer"));

        var title = many
            ? $"{(atLeast ? "At least " : "")}{enabled} programs start with Windows"
            : "The PC is slow to start";
        return ProbeOutcome.Of(SlowdownProbe.Startup, summary, new SlowdownFinding(SlowdownKind.StartupHeavy, title, detail, actions));
    }

    /// <summary>The two Windows blamed most, with how long Windows says each took: "Steam (7.7 s) and OneDrive (3.1 s)".</summary>
    private static string Blame(IReadOnlyList<BootDegradation> slowed, Func<string, ProcessDescriptionEntry?> describe) =>
        FormatHelper.JoinForSentence([.. slowed.Take(2).Select(d => $"{DisplayName(d.Name, describe, [])} ({d.DurationDisplay})")]);

    /// <summary>
    /// What Windows blamed for slowing the last start it timed, longest first. Nothing when that start is older than
    /// <see cref="BlameKeptFor"/>, or when Windows' log could not be read.
    /// </summary>
    /// <remarks>
    /// The time is what ties them together: Windows writes a start's own record and the records of what slowed it in one
    /// pass, with the same time on each. Its log keeps several starts, and a program blamed at an older one is not what
    /// slowed the last.
    /// </remarks>
    internal static IReadOnlyList<BootDegradation> BlamedAtLastStart(BootRecord? lastTimedStart,
                                                                     IReadOnlyList<BootDegradation>? blamed, DateTime now)
    {
        if (lastTimedStart is null || blamed is null || now - lastTimedStart.BootTime > BlameKeptFor) return [];

        return
        [
            .. blamed
                .Where(d => (d.When - lastTimedStart.BootTime).Duration() <= SameStart)
                .OrderByDescending(d => d.DurationMs)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>How much of the memory is in use.</summary>
    internal static ProbeOutcome Memory(SystemSnapshot? snapshot)
    {
        // A memory read that failed leaves the totals at zero rather than throwing, and nothing in use is not a reading.
        if (snapshot is null || snapshot.Memory.TotalGB <= 0) return ProbeOutcome.Unread(SlowdownProbe.Memory);

        var memory = snapshot.Memory;
        // Decided on the number that is shown, so "90% in use" is never printed beside no finding.
        var shown = Math.Round(memory.UsedPercent, MidpointRounding.AwayFromZero);
        var summary = string.Create(CultureInfo.InvariantCulture, $"{shown:F0}% in use");
        if (shown < MemoryFullPercent) return ProbeOutcome.Of(SlowdownProbe.Memory, summary);

        return ProbeOutcome.Of(SlowdownProbe.Memory, summary, new SlowdownFinding(
            SlowdownKind.MemoryFull,
            string.Create(CultureInfo.InvariantCulture, $"Memory is at {shown:F0}%"),
            string.Create(CultureInfo.InvariantCulture,
                $"{memory.UsedGB:F1} GB of {memory.TotalGB:F1} GB in use. Windows starts moving things to the disk above "
                + $"about {MemoryFullPercent:F0}%, and that is when switching between programs begins to stutter."),
            [new("See what is using memory", "nav-processes", "Process Manager")]));
    }

    /// <summary>How long Windows has run since it last restarted.</summary>
    internal static ProbeOutcome Uptime(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return ProbeOutcome.Unread(SlowdownProbe.Uptime);

        var days = (int)snapshot.Os.Uptime.TotalDays;
        var summary = days switch
        {
            0 => "less than a day",
            1 => "1 day",
            _ => $"{days} days",
        };

        // The Health Score's own line: past it, that card recommends a restart, and this one says the same.
        if (HealthScoreService.ComputeUptimeScore(snapshot) > HealthScoreService.RestartRecommendedAtOrBelow)
            return ProbeOutcome.Of(SlowdownProbe.Uptime, summary);

        // No button. Nothing in SysManager restarts the PC, and a button that only said so would be one that does nothing.
        return ProbeOutcome.Of(SlowdownProbe.Uptime, summary, new SlowdownFinding(
            SlowdownKind.LongUptime,
            $"Windows has not restarted in {days} days",
            "Not a problem by itself, and worth knowing: a restart clears a lot of small slowdowns that no tool can tidy "
            + "up while Windows is running. Shutting down may not count: with Fast Startup on, only Restart starts Windows "
            + "afresh.",
            []));
    }

    /// <summary>The findings in the order <see cref="SlowdownKind"/> declares, numbered from 1.</summary>
    internal static IReadOnlyList<SlowdownFinding> Rank(IEnumerable<SlowdownFinding?> findings) =>
        [.. findings.OfType<SlowdownFinding>().OrderBy(f => f.Kind).Select((f, i) => f with { Rank = i + 1 })];
}
