// SysManager · DiskHealthService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>The drives one read found, and the fault that stopped it before the end, if one did.</summary>
/// <param name="Disks">Every drive read, in the order Windows listed them.</param>
/// <param name="Fault">
/// Null when Windows answered the whole read. Otherwise why it stopped, and drives that are there may be missing from
/// <paramref name="Disks"/>: all of them when the Storage provider could not be reached at all. A drive skipped for a
/// field that does not convert does not count: it is the same at every read.
/// </param>
public sealed record DiskHealthRead(IReadOnlyList<DiskHealthReport> Disks, string? Fault = null)
{
    /// <summary>Whether Windows answered the whole read, so what it found can be kept.</summary>
    public bool Complete => Fault is null;
}

/// <summary>
/// Collects disk health via CIM — reads Storage Reliability Counters
/// (which are backed by SMART on real drives) and projects them into a
/// user-friendly verdict ("Healthy / Watch out / Replace soon").
/// No admin required for read-only queries.
/// </summary>
public sealed class DiskHealthService
{
    private readonly Func<DiskHealthRead> _read;
    private readonly Action<string> _logFault;

    // Whether the last read failed, so that a run of failed reads logs its fault once. Guarded by _faultGate: the
    // Dashboard, the health score, Tune-up and the report each read on their own schedule.
    private readonly Lock _faultGate = new();
    private bool _failing;

    /// <summary>Creates the service over Windows' Storage provider.</summary>
    public DiskHealthService() : this(ReadFromWindows) { }

    /// <summary>
    /// Creates the service over <paramref name="read"/>, so a test can make a read fail, and can see what is logged
    /// through <paramref name="logFault"/>, which is the app's log when it is null.
    /// </summary>
    internal DiskHealthService(Func<DiskHealthRead> read, Action<string>? logFault = null)
    {
        _read = read;
        _logFault = logFault ?? LogFault;
    }

    /// <summary>The drives Windows reported, or the ones it reported before a fault, without saying which.</summary>
    /// <remarks>For a caller that judges each drive it is given, and treats an empty list as not checked.</remarks>
    public Task<IReadOnlyList<DiskHealthReport>> CollectAsync(CancellationToken ct = default)
        => Task.Run(() => ReadAndLog().Disks, ct);

    /// <summary>One read of the drives, which says whether Windows answered all of it.</summary>
    /// <remarks>
    /// For a caller that keeps what it reads. The Dashboard kept a read that had failed as if it were the drives: the
    /// drive names on its temperatures for the whole session, and the drive temperatures for 30 seconds (#2782).
    /// </remarks>
    public Task<DiskHealthRead> ReadAsync(CancellationToken ct = default) => Task.Run(ReadAndLog, ct);

    /// <summary>Reads once, and logs the fault unless the read before this one failed too.</summary>
    /// <remarks>
    /// Once per run of failures rather than once per read: the Dashboard reads every 2 seconds, and a fault that
    /// lasts, a WMI service that is switched off for one, would put a line in the log every time and push out of the
    /// rotating log the history a bug report needs. <see cref="NvidiaGpuReader"/> logs a failing read once for the
    /// same reason.
    /// </remarks>
    private DiskHealthRead ReadAndLog()
    {
        var read = _read();
        bool log;
        lock (_faultGate)
        {
            log = !read.Complete && !_failing;
            _failing = !read.Complete;
        }

        if (log && read.Fault is { } fault) _logFault(fault);
        return read;
    }

    private static void LogFault(string fault) =>
        Log.Debug("DiskHealth: the storage provider could not be read: {Error}", fault);

    private static DiskHealthRead ReadFromWindows()
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
            using var searcher = Wmi.Searcher(scope,
                "SELECT __PATH, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
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
                            MediaType = StorageLabels.Media(Convert.ToUInt32(mo["MediaType"] ?? 0u)),
                            BusType = StorageLabels.Bus(Convert.ToUInt32(mo["BusType"] ?? 0u)),
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
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException)
        {
            // Storage scope might not exist on some Windows SKUs, a WMI service that is off or not answering is a
            // COM error, not a management one (#2774), and WMI can deny access without elevation. The drives read
            // before the fault are still right, and the fault says the list may be short.
            return new DiskHealthRead(results, ex.Message);
        }
        return new DiskHealthRead(results);
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
            //
            // The last argument is the enumeration options, which was null — meaning WMI's default, whose
            // timeout is INFINITE. This is the per-disk SMART walk, so a drive whose provider has stopped
            // answering held every caller behind it for as long as SysManager stayed open (#2845).
            using var related = disk.GetRelated(
                "MSFT_StorageReliabilityCounter",
                "MSFT_PhysicalDiskToStorageReliabilityCounter",
                null, null, null, null, false, Wmi.Options());
            foreach (ManagementObject mo in related)
            {
                using (mo)
                {
                    report.TemperatureC = ToDouble(mo["Temperature"]);
                    report.TemperatureMaxC = ToDouble(mo["TemperatureMax"]);
                    var wear = ToInt(mo["Wear"]);
                    if (wear.HasValue) report.WearPercent = wear;
                    report.PowerOnHours = ToLong(mo["PowerOnHours"]);
                    report.ReadErrors = ToCount(mo["ReadErrorsTotal"]);
                    report.WriteErrors = ToCount(mo["WriteErrorsTotal"]);
                    report.StartStopCount = ToLong(mo["StartStopCycleCount"]);
                    return; // one counter per disk
                }
            }
        }
        catch (ManagementException) { /* driver may not expose counters */ }
        catch (UnauthorizedAccessException) { /* WMI access denied */ }
        catch (InvalidOperationException) { /* object lacks a full path to navigate from — treat as no counter */ }
        catch (System.Runtime.InteropServices.COMException) { /* GetRelated can surface a transient WMI COM fault — treat as no counter, don't abort the whole disk enumeration */ }
    }

    /// <summary>
    /// Turn the raw counters into one of four verdicts so the UI can
    /// colour-code each disk at a glance.
    /// </summary>
    private static void ApplyVerdict(DiskHealthReport r)
    {
        // The sentence for what DiskHealthReport.Finding decided, which the health score and the Dashboard judge the
        // drive by too, so the tabs cannot give one drive two answers (#2866).
        switch (r.Finding)
        {
            case DiskFinding.Failing:
                r.Verdict = "Drive is failing — back up now and replace it.";
                r.VerdictColorHex = StatusColors.Bad;
                return;
            case DiskFinding.WindowsWarns:
                r.Verdict = "Drive is warning of problems. Back up soon.";
                r.VerdictColorHex = StatusColors.Warning;
                return;
            case DiskFinding.WornOut:
                r.Verdict = $"SSD {r.WearPercent}% worn out — plan a replacement.";
                r.VerdictColorHex = StatusColors.Warning;
                return;
            case DiskFinding.RunsHot:
                r.Verdict = $"Running hot ({r.TemperatureC:F0} °C). Check cooling / airflow.";
                r.VerdictColorHex = StatusColors.Warning;
                return;
            case DiskFinding.IoErrors:
                r.Verdict = $"{(r.ReadErrors ?? 0) + (r.WriteErrors ?? 0)} I/O errors logged. Monitor closely.";
                r.VerdictColorHex = StatusColors.Warning;
                return;
            case DiskFinding.NothingRead:
                // No status Windows gave that SysManager recognises, and no counter, as without administrator rights.
                // It said "Healthy" in green, from nothing (#2753).
                r.Verdict = "Windows could not report this drive's health.";
                r.VerdictColorHex = StatusColors.Neutral;
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
        r.VerdictColorHex = StatusColors.Good;
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

    /// <summary>A count of events, such as read or write errors, where a reported 0 means none happened.</summary>
    /// <remarks>
    /// It went through <see cref="ToLong"/>, which reads 0 as "not reported", so a healthy drive showed "—" for both
    /// error counts (#2753).
    /// </remarks>
    internal static long? ToCount(object? o)
    {
        if (o is null) return null;
        try { return Convert.ToInt64(o); }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    /// <summary>A figure a driver reports as 0 when it does not keep it, such as the power-on hours.</summary>
    internal static long? ToLong(object? o)
    {
        if (o is null) return null;
        try { var v = Convert.ToInt64(o); return v == 0 ? null : v; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static string MapHealth(uint v) => v switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };
}
