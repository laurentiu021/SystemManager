// SysManager · SystemInfoService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Collects system information via WMI / CIM (no PowerShell spawn needed).
/// PERF-M1: Static data (OS caption, CPU name, disk models) is cached on first
/// query and reused — only dynamic data (CPU load, RAM, uptime) is refreshed.
/// </summary>
/// <remarks>
/// The three DYNAMIC values are syscalls, not WMI. The Landing tab polls this every 300 ms, and each pass
/// used to make two WMI round-trips for numbers the kernel hands over directly:
/// <list type="bullet">
///   <item>CPU load — <c>GetSystemTimes</c> deltas instead of <c>Win32_Processor.LoadPercentage</c>, which is
///   a WMI-throttled one-second average. At a 300 ms poll the chart was redrawing a value that had not
///   changed; the delta is a true average over the interval actually elapsed.</item>
///   <item>Physical memory — <c>GlobalMemoryStatusEx</c>, one syscall, instead of
///   <c>Win32_OperatingSystem.TotalVisibleMemorySize</c>/<c>FreePhysicalMemory</c>.</item>
///   <item>Uptime — <c>Environment.TickCount64</c>, no query at all, and monotonic: the old
///   <c>DateTime.Now - LastBootUpTime</c> warped whenever the clock was changed or DST shifted.</item>
/// </list>
/// The syscalls are constructor-injected so the delta arithmetic is unit-testable without a real machine —
/// the same seam shape <c>ProcessManagerService</c> uses for its window enumeration.
/// </remarks>
public sealed partial class SystemInfoService
{
    // Cached static data — queried once, never changes during app lifetime.
    private OsInfo? _cachedOs;
    private CpuInfo? _cachedCpuStatic;
    private List<DiskInfo>? _cachedDisks;
    private IReadOnlyList<MemoryModule>? _cachedModules;
    private readonly Lock _cacheLock = new();

    private readonly Func<SystemTimes?> _systemTimes;
    private readonly Func<PhysicalMemory?> _physicalMemory;
    private readonly Func<long> _millisecondsSinceBoot;

    // Guards the CPU delta's previous sample. Separate from _cacheLock, and safe to hold across the work it
    // covers because that work is one cheap syscall — the reason the CPU query was moved OUT of _cacheLock
    // was that a WMI round-trip under a shared lock stalls every other caller.
    private readonly Lock _sampleLock = new();
    private SystemTimes? _previousTimes;
    private double _lastLoad = double.NaN;

    public SystemInfoService()
        : this(QuerySystemTimes, QueryPhysicalMemory, static () => Environment.TickCount64) { }

    /// <summary>Test seam: builds the service over supplied syscalls instead of the real ones.</summary>
    /// <param name="systemTimes">Idle/kernel/user tick totals since boot, or null if the syscall failed.</param>
    /// <param name="physicalMemory">Total and available physical bytes, or null if the syscall failed.</param>
    /// <param name="millisecondsSinceBoot">Monotonic milliseconds since the system started.</param>
    /// <remarks>
    /// The CPU baseline is seeded HERE rather than on the first capture, so the first number the user sees is
    /// a real measurement over the interval between construction and that capture. Seeding lazily would have
    /// made the first reading the since-boot average — correct, but not what the machine is doing right now.
    /// </remarks>
    internal SystemInfoService(Func<SystemTimes?> systemTimes, Func<PhysicalMemory?> physicalMemory,
                              Func<long> millisecondsSinceBoot)
    {
        _systemTimes = systemTimes;
        _physicalMemory = physicalMemory;
        _millisecondsSinceBoot = millisecondsSinceBoot;
        _previousTimes = systemTimes();
    }

    /// <summary>Idle, kernel and user tick totals since boot, in 100-nanosecond units.</summary>
    /// <remarks>
    /// <paramref name="Kernel"/> INCLUDES idle time, per <c>GetSystemTimes</c>, so busy time is
    /// <c>(Kernel + User) - Idle</c> rather than a sum of the two non-idle figures.
    /// </remarks>
    internal readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    /// <summary>Total and available physical memory, in bytes.</summary>
    internal readonly record struct PhysicalMemory(ulong TotalBytes, ulong AvailableBytes);

    public Task<SystemSnapshot> CaptureAsync(CancellationToken ct = default)
        => Task.Run(() => Capture(), ct);

    private SystemSnapshot Capture()
    {
        OsInfo osStatic;
        CpuInfo cpuStatic;
        List<DiskInfo> disks;
        IReadOnlyList<MemoryModule> modules;

        lock (_cacheLock)
        {
            // Each Query* returns null on a WMI FAULT (not on a genuine empty result), so the
            // ??= caches ONLY a successful query and RE-QUERIES on the next poll after a
            // transient hiccup. Without this, a WMI fault on the very first CaptureAsync (likely
            // under the startup thundering-herd → RPC-server-too-busy) would cache the fallback
            // defaults for the whole process lifetime — permanently showing "Unknown CPU" /
            // "Windows" / no disks / no RAM even after WMI recovered milliseconds later.
            // A transient default is used for the CURRENT snapshot only and is never cached.
            _cachedOs ??= QueryOsStatic();
            osStatic = _cachedOs ?? new OsInfo("Windows", "", "", TimeSpan.Zero, "");

            // CPU name/cores/threads/clock are static; only the load is dynamic, and it comes from
            // GetSystemTimes below rather than a second Win32_Processor query.
            _cachedCpuStatic ??= QueryCpuStatic();
            cpuStatic = _cachedCpuStatic ?? new CpuInfo("Unknown", 0, 0, 0, 0);

            // Disk info is static (models don't change at runtime).
            _cachedDisks ??= QueryDisks();
            disks = _cachedDisks ?? [];

            // Physical memory modules (bank/manufacturer/capacity/speed/part) are static
            // hardware — enumerate the DIMMs once. Only the dynamic totals refresh below,
            // so the Dashboard's 300 ms vitals poll no longer re-queries Win32_PhysicalMemory.
            _cachedModules ??= QueryMemoryModules();
            modules = _cachedModules ?? [];
        }

        // Outside the lock. The lock exists to serialise the ??= caches and the dynamic values need none of
        // it — while the CPU query was inside, every concurrent CaptureAsync waited on a round-trip it had no
        // interest in. The Dashboard polls this at 300 ms, so the wait was frequent.
        var cpu = cpuStatic with { LoadPercent = SampleCpuLoad() };
        var os = osStatic with { Uptime = TimeSpan.FromMilliseconds(_millisecondsSinceBoot()) };
        return new SystemSnapshot(os, cpu, SampleMemory(modules), disks, DateTime.Now);
    }

    /// <summary>
    /// CPU load as the busy share of the ticks elapsed since the previous sample.
    /// </summary>
    /// <remarks>
    /// Three cases have to answer with something honest rather than zero, because zero is a legible number
    /// that means "idle machine":
    /// <list type="bullet">
    ///   <item>the syscall failed — hold the last known value instead of flashing 0 into the chart;</item>
    ///   <item>no previous sample at all (the constructor's seeding syscall failed too) — report the
    ///   since-boot average, which is what a delta measured from zero actually is;</item>
    ///   <item>two samples inside one timer tick, so the delta is 0 ticks wide — hold the last value, since
    ///   0/0 is not 0% busy.</item>
    /// </list>
    /// </remarks>
    internal double SampleCpuLoad()
    {
        lock (_sampleLock)
        {
            var current = _systemTimes();
            if (current is null) return double.IsNaN(_lastLoad) ? 0 : _lastLoad;

            var now = current.Value;
            var previous = _previousTimes;
            _previousTimes = now;

            if (previous is { } was)
            {
                // Unsigned subtraction: these counters only ever advance, but a spurious backwards step
                // would wrap to an enormous delta and produce a nonsense percentage, so the arithmetic is
                // done in doubles after an explicit ordering check.
                if (now.Idle >= was.Idle && now.Kernel >= was.Kernel && now.User >= was.User)
                {
                    var busyTicks = (now.Kernel - was.Kernel) + (now.User - was.User);
                    if (busyTicks > 0)
                    {
                        _lastLoad = BusyPercent(now.Idle - was.Idle, busyTicks);
                        return _lastLoad;
                    }
                }
            }

            if (double.IsNaN(_lastLoad)) _lastLoad = BusyPercent(now.Idle, now.Kernel + now.User);
            return _lastLoad;
        }
    }

    /// <summary>Busy share of <paramref name="total"/> ticks, clamped to a percentage.</summary>
    private static double BusyPercent(ulong idle, ulong total)
        => total == 0 ? 0 : Math.Clamp((1.0 - (double)idle / total) * 100.0, 0, 100);

    /// <summary>
    /// Physical memory totals from one syscall. The static DIMM inventory is passed through unchanged.
    /// </summary>
    /// <remarks>A failed syscall degrades to zeroed totals with the modules still listed, matching what the
    /// WMI path did when the query faulted.</remarks>
    internal MemoryInfo SampleMemory(IReadOnlyList<MemoryModule> modules)
    {
        var memory = _physicalMemory();
        var totalGB = memory is { } m ? m.TotalBytes / 1024d / 1024d / 1024d : 0;
        var freeGB = memory is { } available ? available.AvailableBytes / 1024d / 1024d / 1024d : 0;
        var usedGB = totalGB - freeGB;
        var pct = totalGB > 0 ? usedGB / totalGB * 100.0 : 0;
        return new MemoryInfo(totalGB, freeGB, usedGB, pct, modules);
    }

    private static SystemTimes? QuerySystemTimes()
    {
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return new SystemTimes(ToTicks(idle), ToTicks(kernel), ToTicks(user));

        Log.Debug("GetSystemTimes failed: {Code}", Marshal.GetLastPInvokeError());
        return null;
    }

    private static ulong ToTicks(NativeMethods.FILETIME time)
        => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    private static PhysicalMemory? QueryPhysicalMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref status))
            return new PhysicalMemory(status.ullTotalPhys, status.ullAvailPhys);

        Log.Debug("GlobalMemoryStatusEx failed: {Code}", Marshal.GetLastPInvokeError());
        return null;
    }

    private static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        // No A/W variants, so no explicit EntryPoint is needed on either.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime,
                                                   out FILETIME lpUserTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

    // Returns null on a WMI fault so the caller's ??= cache retries next poll (see Capture).
    // A successful query always returns a non-null OsInfo (falling through to the loop-less
    // case only when the class returned no rows, which is itself a valid "OS unknown" answer).
    private static OsInfo? QueryOsStatic()
    {
        // A WMI fault (service down, corrupt repository) returns null so it is NOT cached and
        // is retried on the next poll — Capture() supplies a transient default for this snapshot.
        try
        {
            // No LastBootUpTime: this result is CACHED for the process lifetime, so an uptime parsed here
            // would be frozen at first-query time — and Capture overwrites it from the monotonic clock on
            // every snapshot anyway. Selecting and parsing it was work whose result could never be read.
            using var searcher = new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber,OSArchitecture FROM Win32_OperatingSystem");
            using var osCollection = searcher.Get();
            foreach (ManagementObject mo in osCollection)
            {
                using (mo)
                {
                    var caption = mo["Caption"]?.ToString() ?? "Windows";
                    var version = mo["Version"]?.ToString() ?? "";
                    var build = mo["BuildNumber"]?.ToString() ?? "";
                    var arch = mo["OSArchitecture"]?.ToString() ?? "";
                    return new OsInfo(caption, version, build, TimeSpan.Zero, arch);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            /* WMI unavailable — return null so the cache retries (Capture supplies a transient default) */
            return null;
        }
        // Query succeeded but returned no rows — a valid (if unusual) answer; cache it.
        return new OsInfo("Windows", "", "", TimeSpan.Zero, "");
    }

    // Returns null on a WMI fault so the caller's ??= cache retries next poll (see Capture).
    private static CpuInfo? QueryCpuStatic()
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
            /* WMI unavailable — return null so the cache retries (Capture supplies a transient default) */
            return null;
        }
        // Query succeeded but returned no rows — cache the "unknown" answer.
        return new CpuInfo("Unknown", 0, 0, 0, 0);
    }

    // Physical DIMM inventory — static hardware, enumerated once and cached. Kept separate
    // from the dynamic OS query so the per-poll path never re-scans Win32_PhysicalMemory.
    // Returns null on a WMI fault so the caller's ??= cache retries next poll; a successful
    // (even if empty) enumeration returns the list so it is cached.
    private static List<MemoryModule>? QueryMemoryModules()
    {
        List<MemoryModule> modules = [];
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT BankLabel,DeviceLocator,Manufacturer,Capacity,Speed,ConfiguredClockSpeed,PartNumber "
                + "FROM Win32_PhysicalMemory");
            using var modCollection = s.Get();
            foreach (ManagementObject mo in modCollection)
            {
                using (mo)
                {
                    double capBytes = Convert.ToDouble(mo["Capacity"] ?? 0);
                    modules.Add(new MemoryModule(
                        // DeviceLocator is the slot silk-screened on the board ("DIMM0",
                        // "ChannelA-DIMM1"); BankLabel is often just "BANK 0" or empty, so it is
                        // only the fallback. The grid column has always been headed "Slot".
                        mo["DeviceLocator"]?.ToString() ?? mo["BankLabel"]?.ToString() ?? "",
                        mo["Manufacturer"]?.ToString()?.Trim() ?? "",
                        capBytes / 1024d / 1024d / 1024d,
                        Convert.ToUInt32(mo["Speed"] ?? 0u),
                        Convert.ToUInt32(mo["ConfiguredClockSpeed"] ?? 0u),
                        mo["PartNumber"]?.ToString()?.Trim() ?? ""));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            // WMI unavailable. If NOTHING enumerated, return null so the cache retries next poll
            // (a first-call fault must not permanently cache an empty DIMM list). If SOME modules
            // enumerated before the fault, that partial list is a usable answer — cache it.
            return modules.Count > 0 ? modules : null;
        }
        return modules;
    }

    // Returns null on a WMI fault that yields NO disks so the caller's ??= cache retries next
    // poll; a successful (even if empty) enumeration returns the list so it is cached.
    private static List<DiskInfo>? QueryDisks()
    {
        List<DiskInfo> list = [];
        var faulted = false;
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
                /* Win32_DiskDrive also unavailable — both WMI sources faulted */
                faulted = true;
            }
        }
        // If WMI faulted AND nothing enumerated from either source, return null so the cache
        // retries next poll (a first-call fault must not permanently cache an empty disk list).
        // A partial list (some disks enumerated before the fault) is a usable answer — cache it.
        return faulted && list.Count == 0 ? null : list;
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
