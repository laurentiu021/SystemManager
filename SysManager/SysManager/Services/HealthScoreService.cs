// SysManager · HealthScoreService — computes an overall system health score
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Aggregates data from multiple services into a single 0–100 health score.
/// Components weighted:
///   - Disk health (SMART): 25%
///   - Free space on the system drive: 25%
///   - RAM usage: 20%
///   - Uptime: 15%
///   - Battery wear: 15% (only on laptops; redistributed otherwise, to 30/25/25/20)
///
/// No admin required. Read-only queries only.
/// </summary>
/// <remarks>
/// Free space was not a component until it had to be. Without it the score could read "Excellent" on a
/// machine that was out of room — SMART healthy, RAM under 60% and a recent reboot were enough — which is the
/// commonest real cause of "my laptop got slow" and the state that breaks Windows Update and app installs.
/// A flagship number that is confidently wrong costs more trust than a missing feature.
/// <para>Adding it moves every existing score: a machine that read 92 may now read less with nothing
/// changed on it. That is the point, and the changelog says so rather than letting it look like a
/// regression.</para>
/// </remarks>
public sealed class HealthScoreService
{
    private readonly SystemInfoService _sysInfo;
    private readonly DiskHealthService _diskHealth;
    private readonly BatteryService _battery;

    public HealthScoreService(
        SystemInfoService sysInfo,
        DiskHealthService diskHealth,
        BatteryService battery)
    {
        _sysInfo = sysInfo;
        _diskHealth = diskHealth;
        _battery = battery;
    }

    /// <summary>
    /// Computes the health score by querying system info, disk SMART, and battery.
    /// </summary>
    public async Task<HealthScoreResult> ComputeAsync(CancellationToken ct = default)
    {
        // Gather data in parallel
        var sysTask = _sysInfo.CaptureAsync(ct);
        var diskTask = _diskHealth.CollectAsync(ct);
        var batteryTask = _battery.GetBatteryInfoAsync(ct);

        SystemSnapshot? snapshot = null;
        IReadOnlyList<DiskHealthReport>? disks = null;
        BatteryInfo? battery = null;

        // WMI enumeration (Get()) can throw COMException on repository/RPC failures, not
        // just ManagementException — without this arm a transient WMI fault crashes the
        // whole health score instead of degrading to a partial result.
        try { snapshot = await sysTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: system info failed: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warning("HealthScore: system info WMI COM error: 0x{HResult:X8}", ex.HResult); }
        catch (InvalidOperationException ex) { Log.Warning("HealthScore: system info failed: {Error}", ex.Message); }

        try { disks = await diskTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: disk health failed: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warning("HealthScore: disk health WMI COM error: 0x{HResult:X8}", ex.HResult); }
        catch (InvalidOperationException ex) { Log.Warning("HealthScore: disk health failed: {Error}", ex.Message); }

        try { battery = await batteryTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: battery failed: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warning("HealthScore: battery WMI COM error: 0x{HResult:X8}", ex.HResult); }
        catch (InvalidOperationException ex) { Log.Warning("HealthScore: battery failed: {Error}", ex.Message); }

        // Free space is read here rather than injected because FixedDriveService.Enumerate is static and
        // needs no admin — the same shape as the other sources, and ComputeFreeSpaceScore below is pure so
        // the bands stay testable without touching a real disk. It never throws: it skips a drive it cannot
        // read and returns what it got.
        IReadOnlyList<FixedDriveService.FixedDrive> drives = FixedDriveService.Enumerate();

        // Compute component scores
        int diskScore = ComputeDiskScore(disks);
        int freeSpaceScore = ComputeFreeSpaceScore(drives, SystemDriveLetter());
        int ramScore = ComputeRamScore(snapshot);
        int uptimeScore = ComputeUptimeScore(snapshot);
        int batteryScore = ComputeBatteryScore(battery);
        bool hasBattery = battery?.HasBattery ?? false;

        int overall = Combine(diskScore, freeSpaceScore, ramScore, uptimeScore, batteryScore, hasBattery);

        // Build recommendations
        var recommendations = BuildRecommendations(
            diskScore, freeSpaceScore, ramScore, uptimeScore, batteryScore, hasBattery,
            snapshot, disks, battery, drives, SystemDriveLetter());

        // Recorded so a consumer can say "could not read this" instead of reading a verdict out of a
        // fallback number. The scores above already refuse to claim health; this is what makes the reason
        // visible.
        var unavailable = UnavailableComponents(disks, snapshot, drives, SystemDriveLetter());

        return new HealthScoreResult
        {
            Score = overall,
            DiskScore = diskScore,
            FreeSpaceScore = freeSpaceScore,
            RamScore = ramScore,
            UptimeScore = uptimeScore,
            BatteryScore = batteryScore,
            HasBattery = hasBattery,
            Recommendations = recommendations,
            UnavailableComponents = unavailable
        };
    }

    /// <summary>
    /// Which components produced no usable evidence, so a consumer can say "could not read this" instead of
    /// reading a verdict out of a fallback number. The scores already refuse to claim health; this is what
    /// makes the reason visible.
    /// </summary>
    /// <remarks>
    /// Pure and internal for the same reason <see cref="ComputeDiskScore"/> is: the decision is worth
    /// asserting, and asserting it through <see cref="ComputeAsync"/> would mean querying WMI.
    /// <para>Drives present but none readable is the same absence of evidence as no drives at all, and it is
    /// the common case — plenty of consumer SATA and NVMe disks expose nothing through
    /// <c>MSFT_StorageReliabilityCounter</c>, and a VM exposes nothing whatever. Testing only for an empty
    /// list left that machine scored at the deliberate unknown 80, which
    /// <c>DashboardViewModel.ClassifySmartHealth</c> then reads through its <c>&gt;= 60</c> branch as "Disk
    /// health degrading" — the outcome that method's own remarks rule out, because nothing is degrading when
    /// nothing was measured.</para>
    /// <para>Deliberately <c>All</c>, not <c>Any</c>. With one readable drive at 30% and one unreadable,
    /// <c>Any</c> would mark the component unavailable and replace a critical-disk warning with "could not be
    /// read", hiding a failing drive. A mixed read keeps the worst measured verdict. <c>All</c> also covers
    /// the empty list, which is why that case is no longer spelled out.</para>
    /// </remarks>
    internal static List<string> UnavailableComponents(
        IReadOnlyList<DiskHealthReport>? disks, SystemSnapshot? snapshot,
        IReadOnlyList<FixedDriveService.FixedDrive>? drives, string? systemDrive)
    {
        List<string> unavailable = [];
        if (disks is null || disks.All(d => d.HealthPercent is null)) unavailable.Add(DiskComponent);

        // Free space is unavailable when the SYSTEM drive specifically could not be read, not when some
        // drive could not: a BitLocker-locked data volume is skipped by the enumeration and changes nothing
        // about how much room Windows has. The condition mirrors what ComputeFreeSpaceScore falls back on,
        // so the score and the reason cannot disagree.
        if (drives is null || string.IsNullOrWhiteSpace(systemDrive)
            || !drives.Any(d => string.Equals(d.Letter, systemDrive, StringComparison.OrdinalIgnoreCase)
                                && d.SizeGB > 0))
        {
            unavailable.Add(FreeSpaceComponent);
        }

        if (snapshot is null)
        {
            unavailable.Add(MemoryComponent);
            unavailable.Add(UptimeComponent);
        }

        return unavailable;
    }

    /// <summary>Component names used in <see cref="HealthScoreResult.UnavailableComponents"/>.</summary>
    internal const string DiskComponent = "Disk";
    internal const string FreeSpaceComponent = "Free space";
    internal const string MemoryComponent = "Memory";
    internal const string UptimeComponent = "Uptime";

    // ── Component scoring ──────────────────────────────────────────────

    /// <summary>
    /// Combines the component scores into the overall 0–100 figure.
    /// </summary>
    /// <remarks>
    /// Free space carries a quarter, level with disk health, because it is the component most likely to be the
    /// actual problem AND the only one on the list the user can fix today — with tabs this app already owns.
    /// SMART wear and battery wear are real and have no remedy inside SysManager.
    /// <para>A quarter is not a preference, it is the smallest weight that makes the arithmetic honest. A first
    /// draft gave it a fifth, and <c>AFullSystemDrive_CannotScoreGreen</c> caught what that produces: with
    /// every other component perfect, a drive at 99.6% full still totalled 82 on the battery arm, which
    /// <see cref="HealthScoreResult.Label"/> reads as "Good". Escaping that band needs w × 90 &gt; 20, so
    /// anything under 0.223 leaves the headline number able to call a machine that cannot install a Windows
    /// update healthy — the exact defect free space was added to fix.</para>
    /// <para><b>Pure and internal so the weights are testable.</b> They used to be inline here, and the tests
    /// that cared about them re-stated them as literals — which meant changing the real weights failed
    /// nothing. A mutation that dropped free space back to a fifth went green against those tests, and that is
    /// why this method exists: there is now one copy of the weighting and everything reads it.</para>
    /// <para>Every weight is a multiple of five, so the split stays legible when the battery arm
    /// redistributes.</para>
    /// </remarks>
    internal static int Combine(
        int diskScore, int freeSpaceScore, int ramScore, int uptimeScore, int batteryScore, bool hasBattery)
    {
        double total = hasBattery
            ? diskScore * 0.25 + freeSpaceScore * 0.25 + ramScore * 0.20
              + uptimeScore * 0.15 + batteryScore * 0.15
            : diskScore * 0.30 + freeSpaceScore * 0.25 + ramScore * 0.25 + uptimeScore * 0.20;

        return Math.Clamp((int)Math.Round(total), 0, 100);
    }

    /// <summary>The score for a health component whose source produced nothing at all.</summary>
    /// <remarks>
    /// The same value the per-drive unknown rule uses, for the same reason: absent evidence is not a clean
    /// bill of health. It used to be 100 for a missing source, which is how a machine whose disk health could
    /// not be read at all was told "All SMART indicators healthy" — the Dashboard's green branch is `>= 90`.
    /// 80 keeps it out of every green branch while staying out of the alarming range, and
    /// <see cref="HealthScoreResult.UnavailableComponents"/> is what lets a caller word it as unknown rather
    /// than as mildly degraded.
    /// </remarks>
    internal const int UnknownComponentScore = 80;

    internal static int ComputeDiskScore(IReadOnlyList<DiskHealthReport>? disks)
    {
        // Not 100. DiskHealthService swallows ManagementException, UnauthorizedAccessException and
        // COMException and returns its partially-filled list, so a Storage WMI namespace that is broken or
        // access-denied arrives here as an empty list rather than as an exception — indistinguishable, at
        // this point, from a machine with no drives. Either way nothing was measured.
        if (disks is null || disks.Count == 0) return UnknownComponentScore;

        // The worst disk decides. HealthPercent already folds in Windows' own verdict as a ceiling,
        // so there is nothing left to map here.
        //
        // This used to be `?? d.HealthStatus switch { "Healthy" => 100, "Warning" => 50,
        // "Unhealthy" => 20, _ => 80 }`, whose three named arms were unreachable: HealthPercent
        // returns null ONLY when there is no SMART data AND the status is none of those three, so
        // the fallback could only ever hit `_`. It read as if the Windows verdict were handled here
        // while the percentage was quietly ignoring it, and its Warning value (50) disagreed with
        // the real mapping (60).
        //
        // 80, not 100, for a drive we know nothing about: absent evidence is not a clean bill of health.
        // The empty-list case above is a STRONGER absence of evidence and used to be scored more
        // generously — this comment previously claimed a single unknown drive was "the only case that
        // reaches it", which was how the contradiction survived.
        int worstScore = disks.Select(d => d.HealthPercent ?? UnknownComponentScore).Min();
        return Math.Clamp(worstScore, 0, 100);
    }

    /// <summary>
    /// Scores how much room the SYSTEM drive has left. 100 is comfortable, 10 is nearly full.
    /// </summary>
    /// <remarks>
    /// The component the score was missing, and the reason it could read "Excellent" on a machine that was
    /// out of space — the single most common real cause of "my laptop got slow", and the state that actually
    /// breaks Windows Update and app installs. SMART healthy, RAM under 60% and a recent reboot were enough.
    /// <para><b>The system drive only.</b> A full data drive does not slow Windows down; a full C: does. Left
    /// as every-drive it would report a machine as unhealthy for a deliberately-packed archive disk, which is
    /// noise the persona cannot act on.</para>
    /// <para><b>Percent and absolute, because each is wrong alone.</b> Percent punishes a large disk — 10% of
    /// 4 TB is 400 GB and nothing whatever is wrong. Absolute excuses a nearly-full small one — 20 GB free is
    /// comfortable on a 128 GB laptop and nearly empty on a 4 TB archive. So a generous ABSOLUTE amount only
    /// ever raises the percentage verdict, and a hard floor only ever lowers it: Windows needs room to work
    /// regardless of how big the disk is, because a feature update stages around 20 GB and the page file and
    /// temp grow into whatever is left. Below that floor no percentage makes the machine healthy.</para>
    /// <para>The drive letter is a parameter rather than read from the environment, so the bands are testable
    /// without depending on which drive this machine boots from — the same reason the other scorers take
    /// their evidence as an argument.</para>
    /// </remarks>
    internal static int ComputeFreeSpaceScore(IReadOnlyList<FixedDriveService.FixedDrive>? drives, string? systemDrive)
    {
        // Same rule as every other component: absent evidence is not a clean bill of health.
        // FixedDriveService.Enumerate skips a drive it cannot read — a BitLocker-locked volume throws on
        // AvailableFreeSpace — so an unreadable system drive arrives here as an absence, not an exception.
        if (drives is null || drives.Count == 0 || string.IsNullOrWhiteSpace(systemDrive))
            return UnknownComponentScore;

        var drive = drives.FirstOrDefault(d =>
            string.Equals(d.Letter, systemDrive, StringComparison.OrdinalIgnoreCase));

        // SizeGB is rounded to whole gigabytes, so a zero here means either no such drive or one too small
        // to divide by. Neither is a measurement.
        if (drive is null || drive.SizeGB <= 0) return UnknownComponentScore;

        var percentFree = drive.FreeGB / drive.SizeGB * 100;

        var byPercent = percentFree switch
        {
            >= 20 => 100,
            >= 15 => 80,
            >= 10 => 55,
            >= 5 => 25,
            _ => 10
        };

        // Raises only. Below 25 GB it has no opinion and the percentage decides.
        var byAbsolute = drive.FreeGB switch
        {
            >= 50 => 100,
            >= 25 => 80,
            _ => 0
        };

        var score = Math.Max(byPercent, byAbsolute);

        // Lowers only, and it is what stops a small disk being called healthy for having a healthy
        // percentage: 9 GB free on a 40 GB drive is 22% and still not enough for Windows to update itself.
        if (drive.FreeGB < 20) score = Math.Min(score, 55);
        if (drive.FreeGB < 10) score = Math.Min(score, 25);

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// The drive Windows is installed on, as a letter with a colon and no separator — the shape
    /// <see cref="FixedDriveService.FixedDrive.Letter"/> uses.
    /// </summary>
    /// <remarks>
    /// Derived rather than assumed to be C:. Returns null if it cannot be determined, which
    /// <see cref="ComputeFreeSpaceScore"/> treats as "not measured" rather than guessing a letter.
    /// </remarks>
    internal static string? SystemDriveLetter()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return string.IsNullOrWhiteSpace(root) ? null : root.TrimEnd('\\', '/');
    }

    internal static int ComputeRamScore(SystemSnapshot? snapshot)
    {
        // Same rule as the disk: a snapshot that never arrived is not evidence of healthy memory.
        if (snapshot is null) return UnknownComponentScore;
        double usedPct = snapshot.Memory.UsedPercent;

        // Linear scale: 0% used = 100 score, 100% used = 0 score
        // But we're more lenient: up to 60% is fine (score 100), then degrades
        return usedPct switch
        {
            <= 60 => 100,
            <= 70 => 90,
            <= 80 => 75,
            <= 85 => 60,
            <= 90 => 40,
            <= 95 => 20,
            _ => 10
        };
    }

    internal static int ComputeUptimeScore(SystemSnapshot? snapshot)
    {
        // Same rule as the disk and memory arms.
        if (snapshot is null) return UnknownComponentScore;
        double days = snapshot.Os.Uptime.TotalDays;

        return days switch
        {
            <= 3 => 100,
            <= 7 => 90,
            <= 14 => 70,
            <= 21 => 50,
            <= 30 => 30,
            _ => 15
        };
    }

    internal static int ComputeBatteryScore(BatteryInfo? battery)
    {
        if (battery is null || !battery.HasBattery) return 100;

        double health = battery.HealthPercent;
        // -1 means capacity data unavailable (no admin for root\WMI).
        // Return neutral score to avoid false-critical warnings.
        if (health < 0) return 100;

        return health switch
        {
            >= 80 => 100,
            >= 60 => 80,
            >= 40 => 55,
            >= 20 => 30,
            _ => 10
        };
    }

    // ── Recommendations ────────────────────────────────────────────────

    private static List<HealthRecommendation> BuildRecommendations(
        int diskScore, int freeSpaceScore, int ramScore, int uptimeScore, int batteryScore,
        bool hasBattery, SystemSnapshot? snapshot,
        IReadOnlyList<DiskHealthReport>? disks, BatteryInfo? battery,
        IReadOnlyList<FixedDriveService.FixedDrive>? drives, string? systemDrive)
    {
        List<HealthRecommendation> recs = [];

        // Free space FIRST, because only three recommendations are returned and this is the one the user can
        // act on today — with a tab this app already owns. A worn battery and a degrading disk are real, and
        // neither has a fix inside SysManager; a full drive does.
        if (freeSpaceScore < 80 && drives is not null)
        {
            var drive = drives.FirstOrDefault(d =>
                string.Equals(d.Letter, systemDrive, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
            {
                recs.Add(new HealthRecommendation
                {
                    Message = $"Only {drive.FreeGB:0} GB free on {drive.Letter} — Deep Cleanup can reclaim space",
                    Severity = freeSpaceScore <= 25 ? "critical" : "warning"
                });
            }
        }

        // Uptime
        if (uptimeScore <= 70 && snapshot is not null)
        {
            int days = (int)snapshot.Os.Uptime.TotalDays;
            recs.Add(new HealthRecommendation
            {
                Message = $"Restart recommended — {days} days uptime",
                Severity = uptimeScore <= 30 ? "critical" : "warning"
            });
        }

        // Disk
        if (diskScore < 80 && disks is not null)
        {
            var worst = disks.OrderBy(d => d.HealthPercent ?? 100).FirstOrDefault();
            string diskName = worst?.FriendlyName ?? "Disk";
            recs.Add(new HealthRecommendation
            {
                Message = $"{diskName} health degraded — consider backup",
                Severity = diskScore < 50 ? "critical" : "warning"
            });
        }

        // RAM
        if (ramScore < 75 && snapshot is not null)
        {
            recs.Add(new HealthRecommendation
            {
                Message = $"High memory usage ({snapshot.Memory.UsedPercent:0}%) — close unused apps",
                Severity = ramScore <= 40 ? "critical" : "warning"
            });
        }

        // Battery
        if (hasBattery && batteryScore < 80 && battery is not null)
        {
            recs.Add(new HealthRecommendation
            {
                Message = $"Battery wear {battery.WearPercent:0}% — consider replacement",
                Severity = batteryScore < 55 ? "critical" : "warning"
            });
        }

        // Return top 3
        return recs.Take(3).ToList();
    }
}
