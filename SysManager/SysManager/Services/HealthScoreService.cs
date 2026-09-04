// SysManager · HealthScoreService — computes an overall system health score
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Aggregates data from multiple services into a single 0–100 health score.
/// Components weighted:
///   - Disk health: 35%
///   - RAM usage: 25%
///   - Uptime: 20%
///   - Battery wear: 20% (only on laptops; redistributed otherwise)
///
/// No admin required. Read-only queries only.
/// </summary>
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

        // Compute component scores
        int diskScore = ComputeDiskScore(disks);
        int ramScore = ComputeRamScore(snapshot);
        int uptimeScore = ComputeUptimeScore(snapshot);
        int batteryScore = ComputeBatteryScore(battery);
        bool hasBattery = battery?.HasBattery ?? false;

        // Weighted average
        int overall = hasBattery
            ? (int)Math.Round(
                diskScore * 0.35 +
                ramScore * 0.25 +
                uptimeScore * 0.20 +
                batteryScore * 0.20)
            : (int)Math.Round(
                diskScore * 0.40 +
                ramScore * 0.30 +
                uptimeScore * 0.30);

        overall = Math.Clamp(overall, 0, 100);

        // Build recommendations
        var recommendations = BuildRecommendations(
            diskScore, ramScore, uptimeScore, batteryScore, hasBattery, snapshot, disks, battery);

        // Recorded so a consumer can say "could not read this" instead of reading a verdict out of a
        // fallback number. The scores above already refuse to claim health; this is what makes the reason
        // visible.
        var unavailable = UnavailableComponents(disks, snapshot);

        return new HealthScoreResult
        {
            Score = overall,
            DiskScore = diskScore,
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
        IReadOnlyList<DiskHealthReport>? disks, SystemSnapshot? snapshot)
    {
        List<string> unavailable = [];
        if (disks is null || disks.All(d => d.HealthPercent is null)) unavailable.Add(DiskComponent);
        if (snapshot is null)
        {
            unavailable.Add(MemoryComponent);
            unavailable.Add(UptimeComponent);
        }

        return unavailable;
    }

    /// <summary>Component names used in <see cref="HealthScoreResult.UnavailableComponents"/>.</summary>
    internal const string DiskComponent = "Disk";
    internal const string MemoryComponent = "Memory";
    internal const string UptimeComponent = "Uptime";

    // ── Component scoring ──────────────────────────────────────────────

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
        int diskScore, int ramScore, int uptimeScore, int batteryScore,
        bool hasBattery, SystemSnapshot? snapshot,
        IReadOnlyList<DiskHealthReport>? disks, BatteryInfo? battery)
    {
        List<HealthRecommendation> recs = [];

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
