using System.Management;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Collects disk health via CIM — reads Storage Reliability Counters
/// (which are backed by SMART on real drives) and projects them into a
/// user-friendly verdict ("Healthy / Watch out / Replace soon").
/// No admin required for read-only queries.
/// </summary>
public sealed class DiskHealthService
{
    public Task<IReadOnlyList<DiskHealthReport>> CollectAsync(CancellationToken ct = default)
        => Task.Run(() => Collect(), ct);

    private static IReadOnlyList<DiskHealthReport> Collect()
    {
        var results = new List<DiskHealthReport>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            // First pull MSFT_PhysicalDisk for basic info.
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT ObjectId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var report = new DiskHealthReport
                {
                    FriendlyName = mo["FriendlyName"]?.ToString() ?? "Disk",
                    MediaType = MapMedia(Convert.ToUInt32(mo["MediaType"] ?? 0u)),
                    BusType = MapBus(Convert.ToUInt32(mo["BusType"] ?? 0u)),
                    SizeGB = Math.Round(Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d, 0),
                    HealthStatus = MapHealth(Convert.ToUInt32(mo["HealthStatus"] ?? 0u))
                };

                // Get reliability counters for this disk.
                var objectId = mo["ObjectId"]?.ToString();
                if (!string.IsNullOrEmpty(objectId))
                    EnrichWithReliability(scope, objectId, report);

                ApplyVerdict(report);
                results.Add(report);
            }
        }
        catch
        {
            // Scope might not exist on some SKUs; return empty rather than crash.
        }
        return results;
    }

    private static void EnrichWithReliability(ManagementScope scope, string objectId, DiskHealthReport report)
    {
        try
        {
            // Escape quotes & backslashes for the WQL literal.
            var safeId = objectId.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var query = new ObjectQuery(
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{safeId}\"}} WHERE AssocClass=MSFT_PhysicalDiskToStorageReliabilityCounter");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                report.TemperatureC = ToDouble(mo["Temperature"]);
                report.TemperatureMaxC = ToDouble(mo["TemperatureMax"]);
                var wear = ToInt(mo["Wear"]);
                if (wear.HasValue) report.WearPercent = wear;
                report.PowerOnHours = ToLong(mo["PowerOnHours"]);
                report.ReadErrors = ToLong(mo["ReadErrorsTotal"]);
                report.WriteErrors = ToLong(mo["WriteErrorsTotal"]);
                report.StartStopCount = ToLong(mo["StartStopCycleCount"]);
                return; // one counter per disk
            }
        }
        catch { /* driver may not expose counters */ }
    }

    /// <summary>
    /// Turn the raw counters into one of four verdicts so the UI can
    /// colour-code each disk at a glance.
    /// </summary>
    private static void ApplyVerdict(DiskHealthReport r)
    {
        // Base on HealthStatus first — Windows already knows about failures.
        if (r.HealthStatus == "Unhealthy")
        {
            r.Verdict = "Drive is failing — back up now and replace it.";
            r.VerdictColorHex = "#EF4444";
            return;
        }
        if (r.HealthStatus == "Warning")
        {
            r.Verdict = "Drive is warning of problems. Back up soon.";
            r.VerdictColorHex = "#F59E0B";
            return;
        }

        // SMART thresholds
        if (r.WearPercent is >= 90)
        {
            r.Verdict = $"SSD {r.WearPercent}% worn out — plan a replacement.";
            r.VerdictColorHex = "#F59E0B";
            return;
        }
        if (r.TemperatureC is >= 70)
        {
            r.Verdict = $"Running hot ({r.TemperatureC:F0} °C). Check cooling / airflow.";
            r.VerdictColorHex = "#F59E0B";
            return;
        }
        if ((r.ReadErrors ?? 0) > 0 || (r.WriteErrors ?? 0) > 0)
        {
            r.Verdict = $"{(r.ReadErrors ?? 0) + (r.WriteErrors ?? 0)} I/O errors logged. Monitor closely.";
            r.VerdictColorHex = "#F59E0B";
            return;
        }

        // All good
        var bits = new List<string>();
        if (r.TemperatureC.HasValue) bits.Add($"{r.TemperatureC:F0} °C");
        if (r.WearPercent.HasValue) bits.Add($"wear {r.WearPercent}%");
        if (r.PowerOnHours.HasValue) bits.Add($"{r.PowerOnHours} h on");
        r.Verdict = bits.Count > 0
            ? "Healthy — " + string.Join(" · ", bits)
            : "Healthy.";
        r.VerdictColorHex = "#22C55E";
    }

    // ---------- helpers ----------

    private static double? ToDouble(object? o)
    {
        if (o == null) return null;
        try { var v = Convert.ToDouble(o); return Math.Abs(v) < 1e-9 ? null : v; } catch { return null; }
    }

    private static int? ToInt(object? o)
    {
        if (o == null) return null;
        try { return Convert.ToInt32(o); } catch { return null; }
    }

    private static long? ToLong(object? o)
    {
        if (o == null) return null;
        try { var v = Convert.ToInt64(o); return v == 0 ? null : v; } catch { return null; }
    }

    private static string MapMedia(uint v) => v switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified"
    };

    private static string MapBus(uint v) => v switch
    {
        1 => "SCSI", 3 => "ATA", 6 => "Fibre", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 17 => "NVMe", _ => "Other"
    };

    private static string MapHealth(uint v) => v switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown"
    };
}
