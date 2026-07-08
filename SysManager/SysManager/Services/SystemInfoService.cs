// SysManager · SystemInfoService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Collects system information via WMI / CIM (no PowerShell spawn needed).
/// PERF-M1: Static data (OS caption, CPU name, disk models) is cached on first
/// query and reused — only dynamic data (CPU load, RAM, uptime) is refreshed.
/// </summary>
public sealed class SystemInfoService
{
    // Cached static data — queried once, never changes during app lifetime.
    private OsInfo? _cachedOs;
    private CpuInfo? _cachedCpuStatic;
    private List<DiskInfo>? _cachedDisks;
    private IReadOnlyList<MemoryModule>? _cachedModules;
    private readonly Lock _cacheLock = new();

    public Task<SystemSnapshot> CaptureAsync(CancellationToken ct = default)
        => Task.Run(() => Capture(), ct);

    private SystemSnapshot Capture()
    {
        OsInfo osStatic;
        CpuInfo cpu;
        List<DiskInfo> disks;
        IReadOnlyList<MemoryModule> modules;

        lock (_cacheLock)
        {
            // Static OS info (caption, version, build, arch) — cache it; uptime refreshes below.
            _cachedOs ??= QueryOsStatic();
            osStatic = _cachedOs;

            // CPU name/cores/threads/clock are static; only LoadPercentage is dynamic.
            _cachedCpuStatic ??= QueryCpuStatic();
            cpu = _cachedCpuStatic with { LoadPercent = QueryCpuLoad() };

            // Disk info is static (models don't change at runtime).
            _cachedDisks ??= QueryDisks();
            disks = _cachedDisks;

            // Physical memory modules (bank/manufacturer/capacity/speed/part) are static
            // hardware — enumerate the DIMMs once. Only the dynamic totals refresh below,
            // so the Dashboard's 300 ms vitals poll no longer re-queries Win32_PhysicalMemory.
            _cachedModules ??= QueryMemoryModules();
            modules = _cachedModules;
        }

        // One Win32_OperatingSystem query for BOTH the dynamic uptime and memory totals —
        // previously two separate round-trips (QueryUptime + QueryMemory) to the same class.
        var (uptime, mem) = QueryDynamicOs(modules);
        var os = osStatic with { Uptime = uptime };
        return new SystemSnapshot(os, cpu, mem, disks, DateTime.Now);
    }

    private static OsInfo QueryOsStatic()
    {
        // A WMI fault (service down, corrupt repository) must degrade to a safe default, not
        // propagate out of Capture() and surface as an error dialog — mirrors QueryDisks.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime FROM Win32_OperatingSystem");
            using var osCollection = searcher.Get();
            foreach (ManagementObject mo in osCollection)
            {
                using (mo)
                {
                    var caption = mo["Caption"]?.ToString() ?? "Windows";
                    var version = mo["Version"]?.ToString() ?? "";
                    var build = mo["BuildNumber"]?.ToString() ?? "";
                    var arch = mo["OSArchitecture"]?.ToString() ?? "";
                    var lastBootRaw = mo["LastBootUpTime"]?.ToString();
                    var uptime = TimeSpan.Zero;
                    if (!string.IsNullOrEmpty(lastBootRaw))
                    {
                        try { uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(lastBootRaw); }
                        catch (FormatException) { /* WMI date string malformed — keep zero uptime */ }
                        catch (InvalidCastException) { /* WMI returned unexpected type — keep zero uptime */ }
                    }
                    return new OsInfo(caption, version, build, uptime, arch);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — degrade to defaults (mirrors QueryDisks) */
        }
        return new OsInfo("Windows", "", "", TimeSpan.Zero, "");
    }

    // Single Win32_OperatingSystem query for BOTH the dynamic uptime and the memory totals, so
    // the per-poll path makes one round-trip instead of two (QueryUptime + QueryMemory previously
    // queried the same class separately). The static DIMM modules are passed through unchanged.
    private static (TimeSpan Uptime, MemoryInfo Memory) QueryDynamicOs(IReadOnlyList<MemoryModule> modules)
    {
        var uptime = TimeSpan.Zero;
        double totalKb = 0, freeKb = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LastBootUpTime,TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
            using var osCollection = searcher.Get();
            foreach (ManagementObject mo in osCollection)
            {
                using (mo)
                {
                    var lastBootRaw = mo["LastBootUpTime"]?.ToString();
                    if (!string.IsNullOrEmpty(lastBootRaw))
                    {
                        try { uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(lastBootRaw); }
                        catch (FormatException) { /* WMI date string malformed — keep zero uptime */ }
                        catch (InvalidCastException) { /* WMI returned unexpected type — keep zero uptime */ }
                    }
                    totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"] ?? 0);
                    freeKb = Convert.ToDouble(mo["FreePhysicalMemory"] ?? 0);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — degrade to zero uptime + zeroed totals (modules still shown) */
        }

        double totalGB = totalKb / 1024d / 1024d;
        double freeGB = freeKb / 1024d / 1024d;
        double usedGB = totalGB - freeGB;
        double pct = totalGB > 0 ? usedGB / totalGB * 100.0 : 0;
        return (uptime, new MemoryInfo(totalGB, freeGB, usedGB, pct, modules));
    }

    private static CpuInfo QueryCpuStatic()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor");
            using var cpuCollection = searcher.Get();
            foreach (ManagementObject mo in cpuCollection)
            {
                using (mo)
                {
                    return new CpuInfo(
                        mo["Name"]?.ToString()?.Trim() ?? "Unknown CPU",
                        Convert.ToUInt32(mo["NumberOfCores"] ?? 0u),
                        Convert.ToUInt32(mo["NumberOfLogicalProcessors"] ?? 0u),
                        Convert.ToUInt32(mo["MaxClockSpeed"] ?? 0u),
                        0);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — degrade to defaults */
        }
        return new CpuInfo("Unknown", 0, 0, 0, 0);
    }

    private static double QueryCpuLoad()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            using var cpuCollection = searcher.Get();
            foreach (ManagementObject mo in cpuCollection)
            {
                using (mo)
                {
                    return Convert.ToDouble(mo["LoadPercentage"] ?? 0.0);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — degrade to zero load */
        }
        return 0;
    }

    // Physical DIMM inventory — static hardware, enumerated once and cached. Kept separate
    // from the dynamic OS query so the per-poll path never re-scans Win32_PhysicalMemory.
    private static List<MemoryModule> QueryMemoryModules()
    {
        List<MemoryModule> modules = [];
        try
        {
            using var s = new ManagementObjectSearcher("SELECT BankLabel,Manufacturer,Capacity,Speed,PartNumber FROM Win32_PhysicalMemory");
            using var modCollection = s.Get();
            foreach (ManagementObject mo in modCollection)
            {
                using (mo)
                {
                    double capBytes = Convert.ToDouble(mo["Capacity"] ?? 0);
                    modules.Add(new MemoryModule(
                        mo["BankLabel"]?.ToString() ?? "",
                        mo["Manufacturer"]?.ToString()?.Trim() ?? "",
                        capBytes / 1024d / 1024d / 1024d,
                        Convert.ToUInt32(mo["Speed"] ?? 0u),
                        mo["PartNumber"]?.ToString()?.Trim() ?? ""));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — return whatever modules enumerated before the fault */
        }
        return modules;
    }

    private static List<DiskInfo> QueryDisks()
    {
        List<DiskInfo> list = [];
        // Use Storage namespace for MSFT_PhysicalDisk (gives HealthStatus / MediaType)
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var query = new ObjectQuery("SELECT FriendlyName,MediaType,BusType,Size,HealthStatus,OperationalStatus FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var diskCollection = searcher.Get();
            foreach (ManagementObject mo in diskCollection)
            {
                using (mo)
                {
                    var size = Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d;
                    var mediaType = (ushort)Convert.ToUInt32(mo["MediaType"] ?? 0u) switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "Unspecified"
                    };
                    var busType = (ushort)Convert.ToUInt32(mo["BusType"] ?? 0u) switch
                    {
                        1 => "SCSI",
                        2 => "ATAPI",
                        3 => "ATA",
                        4 => "1394",
                        5 => "SSA",
                        6 => "Fibre",
                        7 => "USB",
                        8 => "RAID",
                        9 => "iSCSI",
                        10 => "SAS",
                        11 => "SATA",
                        12 => "SD",
                        13 => "MMC",
                        17 => "NVMe",
                        _ => "Other"
                    };
                    var health = (ushort)Convert.ToUInt32(mo["HealthStatus"] ?? 0u) switch
                    {
                        0 => "Healthy",
                        1 => "Warning",
                        2 => "Unhealthy",
                        _ => "Unknown"
                    };
                    var opStatus = mo["OperationalStatus"] is ushort[] arr && arr.Length > 0
                        ? string.Join(",", arr.Select(OpStatusName))
                        : "Unknown";
                    list.Add(new DiskInfo(
                        mo["FriendlyName"]?.ToString() ?? "Disk",
                        mediaType, busType, size, health, opStatus, null, null));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            // Fallback to Win32_DiskDrive if MSFT_PhysicalDisk / the Storage WMI namespace
            // isn't available (older/headless Windows — scope.Connect() throws COMException).
            // This fallback runs precisely when WMI is already degraded, so it needs its own
            // guard: a fault here (or a malformed Size) must degrade to the partial list, not
            // propagate out of Capture() and surface as an error — mirroring every other method.
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Model,Size,Status FROM Win32_DiskDrive");
                using var fallbackCollection = s.Get();
                foreach (ManagementObject mo in fallbackCollection)
                {
                    using (mo)
                    {
                        try
                        {
                            var size = Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d;
                            list.Add(new DiskInfo(
                                mo["Model"]?.ToString() ?? "Disk",
                                "Unknown", "Unknown", size,
                                mo["Status"]?.ToString() ?? "Unknown",
                                "Unknown", null, null));
                        }
                        catch (Exception exItem) when (exItem is FormatException or OverflowException or InvalidCastException)
                        {
                            /* malformed Size on this disk — skip it, keep the rest */
                        }
                    }
                }
            }
            catch (Exception exFallback) when (exFallback is ManagementException or System.Runtime.InteropServices.COMException)
            {
                /* Win32_DiskDrive also unavailable — return whatever enumerated (mirrors the other WMI methods) */
            }
        }
        return list;
    }

    private static string OpStatusName(ushort v) => v switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "OK",
        4 => "Degraded",
        5 => "Stressed",
        6 => "Predictive Failure",
        7 => "Error",
        8 => "Non-Recoverable Error",
        9 => "Starting",
        10 => "Stopping",
        11 => "Stopped",
        _ => $"Code {v}"
    };
}
