// SysManager · DiskHealthService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using Serilog;
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
        List<DiskHealthReport> results = [];
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            // First pull MSFT_PhysicalDisk for basic info.
            // __PATH must be selected so each returned object carries its full WMI
            // identity — GetRelated (used in EnrichWithReliability to walk the
            // reliability-counter association) needs it; without it the object has an
            // empty relative path and GetRelated throws InvalidOperationException.
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT __PATH, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    // Convert the WMI fields per disk inside a guard: a single disk whose
                    // MediaType/BusType/Size/HealthStatus comes back as an unexpected type
                    // would otherwise throw from Convert.* and abort the whole enumeration,
                    // dropping every other (healthy) disk. Skip the bad disk instead.
                    try
                    {
                        var report = new DiskHealthReport
                        {
                            FriendlyName = mo["FriendlyName"]?.ToString() ?? "Disk",
                            MediaType = MapMedia(Convert.ToUInt32(mo["MediaType"] ?? 0u)),
                            BusType = MapBus(Convert.ToUInt32(mo["BusType"] ?? 0u)),
                            SizeGB = Math.Round(Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d, 0),
                            HealthStatus = MapHealth(Convert.ToUInt32(mo["HealthStatus"] ?? 0u))
                        };

                        // Get reliability counters for this disk by navigating the
                        // CIM association directly from this object — no WQL string,
                        // no ObjectId parsing (the Storage-provider ObjectId format
                        // embeds = and " which broke the old literal-interpolation path).
                        EnrichWithReliability(mo, report);

                        ApplyVerdict(report);
                        results.Add(report);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                    {
                        Log.Debug(ex, "DiskHealth: skipping a disk with an unreadable WMI field");
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Storage scope might not exist on some Windows SKUs.
        }
        catch (UnauthorizedAccessException)
        {
            // WMI access denied without elevation.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // scope.Connect() can throw COMException when the Storage WMI namespace
            // is unavailable (older/headless Windows). Non-fatal — return what we have.
        }
        return results;
    }

    private static void EnrichWithReliability(ManagementObject disk, DiskHealthReport report)
    {
        try
        {
            // Navigate the association from THIS disk to its reliability counter via
            // GetRelated — WMI walks the relationship itself, so we never build a WQL
            // literal from the ObjectId. This is injection-safe by construction and
            // works regardless of the ObjectId format (the Storage-provider form,
            // ...SPACES_PhysicalDisk.ObjectId="{guid}:PD:{guid}", contains = and "
            // that the old regex rejected, silently dropping all SMART data on those
            // machines). An empty result is normal — non-elevated sessions, Storage
            // Spaces topologies, and virtual disks simply expose no counter — so it
            // is NOT logged as a warning (that produced hundreds of WRN lines per
            // session on the ~2s temperature poll).
            using var related = disk.GetRelated(
                "MSFT_StorageReliabilityCounter",
                "MSFT_PhysicalDiskToStorageReliabilityCounter",
                null, null, null, null, false, null);
            foreach (ManagementObject mo in related)
            {
                using (mo)
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
        }
        catch (ManagementException) { /* driver may not expose counters */ }
        catch (UnauthorizedAccessException) { /* WMI access denied */ }
        catch (InvalidOperationException) { /* object lacks a full path to navigate from — treat as no counter */ }
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
        List<string> bits = [];
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
        if (o is null) return null;
        try { var v = Convert.ToDouble(o); return Math.Abs(v) < 1e-9 ? null : v; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static int? ToInt(object? o)
    {
        if (o is null) return null;
        try { return Convert.ToInt32(o); }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static long? ToLong(object? o)
    {
        if (o is null) return null;
        try { var v = Convert.ToInt64(o); return v == 0 ? null : v; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static string MapMedia(uint v) => v switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified"
    };

    private static string MapBus(uint v) => v switch
    {
        1 => "SCSI",
        3 => "ATA",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        _ => "Other"
    };

    private static string MapHealth(uint v) => v switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };
}
