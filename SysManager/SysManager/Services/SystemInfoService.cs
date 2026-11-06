// SysManager · SystemInfoService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Collects system information via WMI / CIM (no PowerShell spawn needed).
/// Static data (OS caption, CPU name, disk models) is cached on first
/// query and reused — only dynamic data (CPU load, RAM, uptime) is refreshed (#406).
/// </summary>
/// <remarks>
/// The three DYNAMIC values are syscalls, not WMI. The Landing tab polls this every 300 ms, and each pass
/// used to make two WMI round-trips for numbers the kernel hands over directly:
/// <list type="bullet">
///   <item>CPU load — <c>GetSystemTimes</c> deltas instead of <c>Win32_Processor.LoadPercentage</c>, which is
///   a WMI-throttled one-second average. At a 300 ms poll the chart was redrawing a value that had not
///   changed; the delta is a true average over the interval actually elapsed, each caller's own: a caller that
///   shows the load measures it with a <see cref="CpuLoadSampler"/> of its own (#2790).</item>
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
    // Cached static data — queried once, never changes during app lifetime. The disks are the exception: their
    // list carries each disk's health, which is read again every DiskRefreshInterval.
    private OsInfo? _cachedOs;
    private CpuInfo? _cachedCpuStatic;
    private List<DiskInfo>? _cachedDisks;
    private IReadOnlyList<MemoryModule>? _cachedModules;
    private readonly Lock _cacheLock = new();

    /// <summary>How often the disks, and with them each disk's health, are read again.</summary>
    /// <remarks>
    /// They were read once per session, so a disk that turned unhealthy while SysManager ran was never noticed: the
    /// tray's check every minute and System Health's Windows Health column both read its health from the start
    /// (#2732). A few minutes keeps the query, a WMI round trip, off the vitals poll.
    /// </remarks>
    internal static readonly TimeSpan DiskRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;

    // When the cached disks were read, from _time, and whether a caller is reading them again. Guarded by _cacheLock.
    private long _disksReadAt;
    private bool _diskRefreshInFlight;

    // Claimed by whichever caller finds the cache still empty, so the four WMI round trips below run on
    // that ONE caller's thread and nobody else's — a concurrent caller sees this already true and skips
    // straight to the syscalls instead of joining the wait (#2845).
    private bool _fillInFlight;

    private readonly Func<SystemTimes?> _systemTimes;
    private readonly Func<PhysicalMemory?> _physicalMemory;
    private readonly Func<long> _millisecondsSinceBoot;
    private readonly Func<OsInfo?> _queryOsStatic;
    private readonly Func<CpuInfo?> _queryCpuStatic;
    private readonly Func<List<DiskInfo>?> _queryDisks;
    private readonly Func<IReadOnlyList<MemoryModule>?> _queryMemoryModules;

    // The load a sampler from NewCpuSampler last measured over a real interval, which is what a capture without a
    // sampler of its own shows. NaN until one has. Written by whichever thread sampled, hence Volatile.
    private double _latestLoad = double.NaN;

    public SystemInfoService()
        : this(QuerySystemTimes, QueryPhysicalMemory, static () => Environment.TickCount64) { }

    /// <summary>Test seam: builds the service over supplied syscalls instead of the real ones.</summary>
    /// <param name="systemTimes">Idle/kernel/user tick totals since boot, or null if the syscall failed.</param>
    /// <param name="physicalMemory">Total and available physical bytes, or null if the syscall failed.</param>
    /// <param name="millisecondsSinceBoot">Monotonic milliseconds since the system started.</param>
    /// <param name="queryOsStatic">Replaces the real <c>Win32_OperatingSystem</c> read (#2845's query seam).</param>
    /// <param name="queryCpuStatic">Replaces the real <c>Win32_Processor</c> read.</param>
    /// <param name="queryDisks">Replaces the real disk enumeration.</param>
    /// <param name="queryMemoryModules">Replaces the real <c>Win32_PhysicalMemory</c> read.</param>
    /// <param name="time">Times the disks' refresh; the real clock when null.</param>
    internal SystemInfoService(Func<SystemTimes?> systemTimes, Func<PhysicalMemory?> physicalMemory,
                              Func<long> millisecondsSinceBoot,
                              Func<OsInfo?>? queryOsStatic = null,
                              Func<CpuInfo?>? queryCpuStatic = null,
                              Func<List<DiskInfo>?>? queryDisks = null,
                              Func<IReadOnlyList<MemoryModule>?>? queryMemoryModules = null,
                              TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _systemTimes = systemTimes;
        _physicalMemory = physicalMemory;
        _millisecondsSinceBoot = millisecondsSinceBoot;
        _queryOsStatic = queryOsStatic ?? QueryOsStatic;
        _queryCpuStatic = queryCpuStatic ?? QueryCpuStatic;
        _queryDisks = queryDisks ?? QueryDisks;
        _queryMemoryModules = queryMemoryModules ?? QueryMemoryModules;
    }

    /// <summary>Idle, kernel and user tick totals since boot, in 100-nanosecond units.</summary>
    /// <remarks>
    /// <paramref name="Kernel"/> INCLUDES idle time, per <c>GetSystemTimes</c>, so busy time is
    /// <c>(Kernel + User) - Idle</c> rather than a sum of the two non-idle figures.
    /// </remarks>
    internal readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    /// <summary>Total and available physical memory, in bytes.</summary>
    internal readonly record struct PhysicalMemory(ulong TotalBytes, ulong AvailableBytes);

    /// <summary>A CPU load sampler with a baseline of its own, seeded now.</summary>
    /// <remarks>
    /// One per caller that shows the load, kept for as long as it samples: its first sample then measures the interval
    /// from here, and each one after it the interval since its own previous sample, whoever else captures meanwhile.
    /// </remarks>
    public CpuLoadSampler NewCpuSampler() => new(_systemTimes, load => Volatile.Write(ref _latestLoad, load));

    /// <summary>A snapshot whose CPU load is the latest any sampler measured, without taking a sample.</summary>
    /// <remarks>
    /// For a caller that shows no load of its own, such as the static details or a report: taking a sample here would
    /// measure nothing of interest, and it is no one's baseline to move. Before any sampler has measured, the load is
    /// the average since Windows started.
    /// </remarks>
    public Task<SystemSnapshot> CaptureAsync(CancellationToken ct = default)
        => Task.Run(() => Capture(cpu: null), ct);

    /// <summary>A snapshot whose CPU load is <paramref name="cpu"/>'s next sample.</summary>
    public Task<SystemSnapshot> CaptureAsync(CpuLoadSampler cpu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        return Task.Run(() => Capture(cpu), ct);
    }

    /// <summary>
    /// A snapshot taken on the calling thread, whose CPU load is <paramref name="cpu"/>'s next sample, or without one
    /// the latest a sampler measured.
    /// </summary>
    /// <remarks>
    /// For a loop that already runs on the thread pool, where
    /// <see cref="CaptureAsync(CpuLoadSampler, CancellationToken)"/> would hand the same work to another pool thread
    /// and wait for it (#2854). It reads WMI the first time and when the disks are due, so nothing on the UI thread
    /// calls it: <c>ArchitectureTests.OnlyThePoolLoopsCaptureInPlace</c> names the callers.
    /// </remarks>
    internal SystemSnapshot Capture(CpuLoadSampler? cpu)
    {
        OsInfo? osStatic;
        CpuInfo? cpuStatic;
        List<DiskInfo>? disks;
        IReadOnlyList<MemoryModule>? modules;
        bool iAmFilling;
        bool iAmRefreshingDisks;

        lock (_cacheLock)
        {
            osStatic = _cachedOs;
            cpuStatic = _cachedCpuStatic;
            disks = _cachedDisks;
            modules = _cachedModules;

            // Claim the fill only if the cache still needs it AND nobody else is already running it. A
            // concurrent caller that sees _fillInFlight already true below falls straight through to the
            // syscalls with whatever is cached right now (a transient default, on the very first poll) —
            // it never queues up behind this thread's WMI round trips (#2845).
            iAmFilling = (osStatic is null || cpuStatic is null || disks is null || modules is null)
                         && !_fillInFlight;
            if (iAmFilling) _fillInFlight = true;

            // The same single claim for reading the disks again, so their health is the one Windows reports now.
            iAmRefreshingDisks = disks is not null && !_diskRefreshInFlight
                                 && _time.GetElapsedTime(_disksReadAt) >= DiskRefreshInterval;
            if (iAmRefreshingDisks) _diskRefreshInFlight = true;
        }

        if (iAmFilling)
        {
            // The four round trips run here, with NO lock held — a concurrent Capture() reading the cache
            // right now does not wait on any of this. Each Query* returns null on a WMI FAULT (not on a
            // genuine empty result), so a transient hiccup (likely under the startup thundering-herd →
            // RPC-server-too-busy) is retried on a LATER poll instead of being cached as the permanent
            // "Unknown CPU" / "Windows" / no disks / no RAM answer.
            osStatic ??= _queryOsStatic();
            cpuStatic ??= _queryCpuStatic();
            disks ??= _queryDisks();
            modules ??= _queryMemoryModules();

            lock (_cacheLock)
            {
                _cachedOs ??= osStatic;
                _cachedCpuStatic ??= cpuStatic;
                if (_cachedDisks is null && disks is not null) _disksReadAt = _time.GetTimestamp();
                _cachedDisks ??= disks;
                _cachedModules ??= modules;
                _fillInFlight = false;
            }
        }

        if (iAmRefreshingDisks)
        {
            // A fault keeps the disks as last read, and waits the interval like a read that worked, rather than
            // asking WMI again on every capture.
            var fresh = _queryDisks();
            lock (_cacheLock)
            {
                if (fresh is not null) _cachedDisks = disks = fresh;
                _disksReadAt = _time.GetTimestamp();
                _diskRefreshInFlight = false;
            }
        }

        // A transient default is used for THIS snapshot only and is never cached — Capture() above already
        // decided what gets published.
        var os = (osStatic ?? new OsInfo("Windows", "", "", TimeSpan.Zero, ""))
            with
        { Uptime = TimeSpan.FromMilliseconds(_millisecondsSinceBoot()) };

        // CPU name/cores/threads/clock are static; only the load is dynamic, and it comes from
        // GetSystemTimes rather than a second Win32_Processor query — and never waits on the lock above.
        var processor = (cpuStatic ?? new CpuInfo("Unknown", 0, 0, 0, 0)) with { LoadPercent = CpuLoad(cpu) };
        return new SystemSnapshot(os, processor, SampleMemory(modules ?? []), disks ?? [], DateTime.Now);
    }

    /// <summary><paramref name="cpu"/>'s next sample, or without one the latest a sampler measured.</summary>
    private double CpuLoad(CpuLoadSampler? cpu)
    {
        if (cpu is not null) return cpu.Sample();

        var latest = Volatile.Read(ref _latestLoad);
        if (!double.IsNaN(latest)) return latest;
        return _systemTimes() is { } sinceBoot
            ? CpuLoadSampler.BusyPercent(sinceBoot.Idle, sinceBoot.Kernel + sinceBoot.User)
            : 0;
    }

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
            using var searcher = Wmi.Searcher("SELECT Caption,Version,BuildNumber,OSArchitecture FROM Win32_OperatingSystem");
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
            using var searcher = Wmi.Searcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor");
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
            using var s = Wmi.Searcher(
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
            using var searcher = Wmi.Searcher(scope,
                "SELECT FriendlyName,MediaType,BusType,Size,HealthStatus,OperationalStatus FROM MSFT_PhysicalDisk");
            using var diskCollection = searcher.Get();
            foreach (ManagementObject mo in diskCollection)
            {
                using (mo)
                {
                    var size = Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d;
                    var mediaType = StorageLabels.Media(Convert.ToUInt32(mo["MediaType"] ?? 0u));
                    var busType = StorageLabels.Bus(Convert.ToUInt32(mo["BusType"] ?? 0u));
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
                using var s = Wmi.Searcher("SELECT Model,Size,Status FROM Win32_DiskDrive");
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

    /// <summary>A disk's operational status, named by the CIM value map MSFT_PhysicalDisk uses.</summary>
    /// <remarks>
    /// Every name from 2 on used to sit one value too late, so a healthy disk read "Unknown" (#2753).
    /// </remarks>
    internal static string OpStatusName(ushort v) => v switch
    {
        0 => "Unknown",
        1 => "Other",
        2 => "OK",
        3 => "Degraded",
        4 => "Stressed",
        5 => "Predictive Failure",
        6 => "Error",
        7 => "Non-Recoverable Error",
        8 => "Starting",
        9 => "Stopping",
        10 => "Stopped",
        11 => "In Service",
        12 => "No Contact",
        13 => "Lost Communication",
        14 => "Aborted",
        15 => "Dormant",
        16 => "Supporting Entity in Error",
        17 => "Completed",
        18 => "Power Mode",
        19 => "Relocating",
        _ => $"Code {v}"
    };
}
