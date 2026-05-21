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

        try { snapshot = await sysTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: system info failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("HealthScore: system info failed: {Error}", ex.Message); }

        try { disks = await diskTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: disk health failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("HealthScore: disk health failed: {Error}", ex.Message); }

        try { battery = await batteryTask.ConfigureAwait(false); }
        catch (System.Management.ManagementException ex) { Log.Warning("HealthScore: battery failed: {Error}", ex.Message); }
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

        return new HealthScoreResult
        {
            Score = overall,
            DiskScore = diskScore,
            RamScore = ramScore,
            UptimeScore = uptimeScore,
            BatteryScore = batteryScore,
            HasBattery = hasBattery,
            Recommendations = recommendations
        };
    }

    // ── Component scoring ──────────────────────────────────────────────

    internal static int ComputeDiskScore(IReadOnlyList<DiskHealthReport>? disks)
    {
        if (disks is null || disks.Count == 0) return 100;

        // Use the worst disk's health percentage, or map status string
        int worstScore = disks.Select(d => d.HealthPercent ?? d.HealthStatus switch
        {
            "Healthy" => 100,
            "Warning" => 50,
            "Unhealthy" => 20,
            _ => 80
        }).Min();
        return Math.Clamp(worstScore, 0, 100);
    }

    internal static int ComputeRamScore(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return 100;
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
        if (snapshot is null) return 100;
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
