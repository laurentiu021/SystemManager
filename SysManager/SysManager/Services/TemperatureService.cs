// SysManager · TemperatureService — aggregates CPU, GPU, and disk temperatures
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using LibreHardwareMonitor.Hardware;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads temperatures from all available sensors.
/// With admin: LibreHardwareMonitor gives the GPU and disk temps, and the CPU and motherboard temps when the
/// separately installed PawnIO driver is there.
/// Without admin: NVIDIA GPU (via NvAPIWrapper) + Disk SMART temps only.
/// </summary>
public sealed class TemperatureService : IDisposable
{
    private readonly DiskHealthService _diskHealth;
    private readonly bool _skipHardwareInit;

    // LibreHardwareMonitor's Computer.Open() enumerates all hardware and, when the
    // PawnIO driver is installed, loads its CPU and motherboard modules into it — far
    // too heavy to do on every 2s poll. Open it once
    // (lazily, on the first elevated read) and keep it alive for the service
    // lifetime; each poll then only calls Update(). The lock serialises the
    // not-thread-safe native sensor access of the LibreHardwareMonitor path (admin), in
    // case more than one caller polls at once (the 2s temperature poll, the user's
    // Refresh command, and the always-on 10s resource sampler all share this service).
    // NVIDIA's API has its own single owner and its own lock — see INvidiaGpuReader.
    private readonly Lock _sensorLock = new();
    private readonly INvidiaGpuReader? _nvidia;
    private Computer? _computer;
    private bool _disposed;

    // Disk friendly-names are static hardware identity — they never change during a session,
    // yet the Dashboard's 2s temperature poll (includeStorage=true) re-resolved them every tick
    // via a Win32_DiskDrive WMI query AND a DiskHealthService SMART-association walk (by far the
    // heaviest part of a read). Memoize both once — mirroring the _nvApiInitTried "resolve static
    // hardware once" pattern — so the hot poll only calls LHM Update() for live temps.
    private List<string>? _cachedWmiDiskNames;      // GetDiskNamesFromWmi(), guarded by _sensorLock
    private List<string>? _cachedStorageFriendlyNames; // DiskHealthService friendly names, guarded by _enrichGate
    private readonly SemaphoreSlim _enrichGate = new(1, 1);

    // Whether the LibreHardwareMonitor open has been attempted. Guarded by _sensorLock, which is held
    // wherever this is read or written. Mirrors _nvApiInitTried below: the point is that a FAILED attempt
    // counts as an attempt, which ??= alone cannot express.
    private bool _lhmInitTried;

    private readonly TimeProvider _timeProvider;

    // The non-admin storage read, with a short TTL. The elevated arm memoizes its SMART walk for the session;
    // this arm had no cache at all, so the 2s Dashboard poll paid a full Storage-namespace connect plus one
    // SMART association walk per disk, 30 times a minute. Guarded by _enrichGate.
    private List<TemperatureReading>? _cachedDiskTemperatures;
    private long _diskTemperaturesStamp;

    // The sensor TOPOLOGY — which hardware exists and how many temperature sensors each exposes — is
    // static hardware identity, exactly like the disk names above, so it is logged once per session
    // instead of once per hardware item per poll. It was 4 lines every 2 seconds (the Dashboard's
    // temperature poll, DashboardViewModel.cs:379), i.e. ~2 lines/second for as long as the app runs.
    //
    // That matters because the rotating log is the ONLY diagnostic a user can send, and it is the only
    // evidence available when the app dies before showing a window. The release smoke-check dumps the
    // last 40 lines on failure; on v1.65.6 all 40 were this one message, so a real fault would have
    // been pushed out of the window by noise. Bounded at 10 MB x 14 files (LogService), so the spam
    // also evicts genuine history.
    //
    // Guarded by _sensorLock, which is already held wherever this is read or written.
    private bool _loggedSensorTopology;

    /// <summary>
    /// Creates the reader. No hardware is touched until the first read.
    /// </summary>
    /// <param name="diskHealth">Source of the SMART/temperature walk.</param>
    /// <param name="skipHardwareInit">Set by tests so no hardware is opened.</param>
    /// <param name="nvidia">
    /// The app's one reader of NVIDIA's API, which the container always passes (#2829). Null reads no NVIDIA GPU,
    /// which is what a test without that driver would get anyway, and keeps this class from ever building a reader
    /// of its own — a second one would set that global API up again, unsynchronised with the app's.
    /// </param>
    /// <param name="timeProvider">
    /// Clock for the storage-temperature time-to-live. Optional with a System default, like
    /// <paramref name="skipHardwareInit"/>, so the ten existing construction sites are unaffected while a test
    /// can still drive the TTL without sleeping.
    /// </param>
    public TemperatureService(DiskHealthService diskHealth, bool skipHardwareInit = false,
        TimeProvider? timeProvider = null, INvidiaGpuReader? nvidia = null)
    {
        _diskHealth = diskHealth;
        _skipHardwareInit = skipHardwareInit;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nvidia = nvidia;
    }

    /// <summary>
    /// Reads all available temperature sensors. When <paramref name="includeStorage"/> is
    /// false, the disk-temperature paths are skipped — the storage-name WMI lookup and the
    /// per-disk SMART enumeration (<see cref="DiskHealthService.CollectAsync"/>) are by far
    /// the heaviest part of a read, so a fast caller that only needs CPU/GPU (e.g. the
    /// always-on resource sampler polling every 10s) passes false to avoid that cost.
    /// </summary>
    public async Task<List<TemperatureReading>> ReadAllAsync(bool includeStorage = true)
    {
        if (_skipHardwareInit) return [];

        List<TemperatureReading> readings = [];
        var isAdmin = AdminHelper.IsElevated();

        if (isAdmin)
        {
            await Task.Run(() => ReadViaLibreHardwareMonitor(readings, includeStorage)).ConfigureAwait(false);

            // LHM storage often has bad names — enrich from DiskHealthService (skipped on the fast path).
            if (includeStorage)
                await EnrichStorageNamesAsync(readings).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => ReadNvidiaGpuTemperatures(readings)).ConfigureAwait(false);
            if (includeStorage)
                await ReadDiskTemperaturesAsync(readings).ConfigureAwait(false);
        }

        AddCpuPlaceholder(readings, isAdmin, CpuDriverInstalled.Value);
        return readings;
    }

    /// <summary>
    /// Adds the CPU row that says why there is no CPU temperature, when none was read.
    /// </summary>
    /// <remarks>
    /// LibreHardwareMonitor 0.9.6 ships no driver of its own: it reads the CPU and the motherboard through the
    /// PawnIO driver, which is installed separately and which SysManager does not install. Without it an elevated
    /// read returned no CPU row at all, so the CPU temperature that running as administrator promised was simply
    /// gone after the relaunch (#2828). The driver is named whether or not SysManager is elevated, since
    /// elevation alone cannot help; with the driver there, a read that is not elevated says administrator.
    /// </remarks>
    internal static void AddCpuPlaceholder(List<TemperatureReading> readings, bool isElevated, bool cpuDriverInstalled)
    {
        if (readings.Any(r => r.Component == "CPU" && r.TemperatureC.HasValue)) return;

        if (!cpuDriverInstalled)
            readings.Add(new TemperatureReading("CPU", "CPU Package", null, RequiresDriver: true));
        else if (!isElevated)
            readings.Add(new TemperatureReading("CPU", "CPU Package", null, RequiresAdmin: true));
    }

    // LibreHardwareMonitor reads PawnIO's uninstall entry once, in PawnIo's static constructor, so the answer
    // cannot change while SysManager runs and is read once here too.
    private static readonly Lazy<bool> CpuDriverInstalled = new(ReadCpuDriverInstalled);

    private static bool ReadCpuDriverInstalled()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        }
        catch (TypeInitializationException ex)
        {
            // The registry read in PawnIo's static constructor failed. LibreHardwareMonitor's own CPU code goes
            // through the same type, so it cannot reach the driver either: report it as missing.
            Log.Debug("Could not tell whether the PawnIO driver is installed: {Error}", ex.Message);
            return false;
        }
    }

    private void ReadViaLibreHardwareMonitor(List<TemperatureReading> readings, bool includeStorage = true)
    {
        lock (_sensorLock)
        {
            if (_disposed) return;
            try
            {
                // Open the hardware monitor once and reuse it; subsequent polls
                // only Update() the already-enumerated hardware. Attempted at most ONCE per session:
                // ??= latches on success only, so a failing open used to be retried on every 2s poll.
                if (!_lhmInitTried)
                {
                    _lhmInitTried = true;
                    _computer = TryOpenComputer();
                }

                if (_computer is null) return;

                // Pre-fetch disk names from WMI for cross-reference (skipped on the fast path —
                // the sampler doesn't need names). Memoized: disk models are static hardware, so
                // resolve the Win32_DiskDrive query once and reuse it — the 2s poll no longer
                // re-queries WMI every tick. Guarded by _sensorLock (already held here).
                // ?? [] after the ??=, not instead of it: a null means the query faulted, so nothing is
                // cached and the next poll retries, while this poll proceeds with no names rather than none of
                // the other sensors.
                var diskNames = includeStorage
                    ? ((_cachedWmiDiskNames ??= GetDiskNamesFromWmi()) ?? [])
                    : [];

                ReadHardware(_computer.Hardware, readings, includeStorage, diskNames);
            }
            catch (Exception ex)
            {
                // Broad on purpose: LibreHardwareMonitor reads each sensor through PawnIO, a GPU vendor's library or
                // Windows, which fail with whatever the hardware layer reports, and a failed read must leave the rest
                // of the poll intact.
                Log.Debug("LibreHardwareMonitor failed: {Error}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads the temperatures of <paramref name="hardwareItems"/> into <paramref name="readings"/>.
    /// </summary>
    /// <remarks>
    /// Called with <see cref="_sensorLock"/> held. Internal so a test can hand it stand-in hardware: the real list
    /// comes from a <see cref="Computer"/>, which needs administrator rights and real sensors.
    /// </remarks>
    internal void ReadHardware(IEnumerable<IHardware> hardwareItems, List<TemperatureReading> readings,
        bool includeStorage, IReadOnlyList<string> diskNames)
    {
        foreach (var hardware in hardwareItems)
        {
            // The fast path does not read storage, so it does not update it either. Updating a disk is a SMART
            // pass-through on every NVMe, SATA and USB drive, which the 10-second sampler paid for and then threw
            // away whenever SysManager ran elevated, with its window hidden too (#2828).
            var skipped = !includeStorage && hardware.HardwareType == HardwareType.Storage;
            if (!skipped)
            {
                hardware.Update();

                foreach (var subHardware in hardware.SubHardware)
                    subHardware.Update();
            }

            // Topology once per session, not once per hardware item per 2s poll — see
            // _loggedSensorTopology. The diagnostic value is "what sensors does this machine
            // expose", which is answered by the first read and unchanged by the 30th.
            if (!_loggedSensorTopology)
            {
                Log.Debug("LHM: {Type} '{Name}' — {SensorCount} temp sensors",
                    hardware.HardwareType, hardware.Name,
                    hardware.Sensors.Count(s => s.SensorType == SensorType.Temperature));
            }

            if (skipped) continue;

            var component = hardware.HardwareType switch
            {
                HardwareType.Cpu => "CPU",
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
                HardwareType.Storage => "Storage",
                HardwareType.Motherboard => "Motherboard",
                _ => null
            };

            if (component is null) continue;

            var tempSensors = hardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value > 0)
                .ToList();

            // Also check sub-hardware (e.g. motherboard chips)
            foreach (var sub in hardware.SubHardware)
            {
                tempSensors.AddRange(sub.Sensors
                    .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value > 0));
            }

            if (component == "CPU")
            {
                AddCpuReadings(hardware.Name, tempSensors, readings);
            }
            else if (component == "GPU")
            {
                var gpuTemp = tempSensors.FirstOrDefault(s =>
                    s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)) ?? tempSensors.FirstOrDefault();

                if (gpuTemp is not null)
                {
                    readings.Add(new TemperatureReading("GPU Core",
                        hardware.Name, gpuTemp.Value));
                }

                var hotSpot = tempSensors.FirstOrDefault(s =>
                    s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase));
                if (hotSpot is not null)
                {
                    readings.Add(new TemperatureReading("GPU Hot Spot",
                        hardware.Name, hotSpot.Value));
                }
            }
            else if (component == "Storage")
            {
                var diskTemp = tempSensors.FirstOrDefault();
                if (diskTemp is not null)
                {
                    var name = hardware.Name;

                    // LHM often returns empty or cryptic names for storage
                    var namedByLhm = !(string.IsNullOrWhiteSpace(name) || name.Length <= 3 || name.All(char.IsDigit));
                    if (!namedByLhm)
                    {
                        // Try matching by index from WMI disk list
                        var storageIndex = readings.Count(r => r.Component == "Storage");
                        name = storageIndex < diskNames.Count
                            ? diskNames[storageIndex]
                            : $"Drive {storageIndex + 1}";
                    }

                    // The flag travels with the reading instead of being re-derived downstream. The
                    // enricher used to overwrite EVERY storage name by list position, clobbering both
                    // a good LHM name and the WMI model just substituted above.
                    readings.Add(new TemperatureReading("Storage", name, diskTemp.Value,
                        NameIsPlaceholder: !namedByLhm));
                }
            }
            else if (component == "Motherboard")
            {
                foreach (var sub in hardware.SubHardware)
                {
                    var chipTemp = sub.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value > 0)
                        .FirstOrDefault();
                    if (chipTemp is not null)
                    {
                        readings.Add(new TemperatureReading("Motherboard", $"{sub.Name}", chipTemp.Value));
                    }
                }
            }
        }

        // Set only after the loop completed, so a read that threw partway through can log the
        // remaining hardware on its next attempt rather than losing it for the session.
        _loggedSensorTopology = true;
    }

    /// <summary>
    /// Adds the CPU's package temperature and its hottest core, read from <paramref name="sensors"/>.
    /// </summary>
    /// <remarks>
    /// Not every Temperature sensor LibreHardwareMonitor reports for a CPU says how hot it is. Intel processors get
    /// one "Distance to TjMax" per core, TjMax minus that core's temperature, and a Zen or Zen+ processor with an
    /// offset gets "Core (Tctl)", its fan-control value, beside the real "Core (Tdie)". "Hottest Core" was the
    /// highest of every sensor named Core, so a 38 °C CPU with a TjMax of 100 showed 62 °C, and Resource History,
    /// which records the higher of the two CPU rows, never drew its CPU line below that (#2828). Neither is read.
    /// <para>The hottest core is picked among the cores themselves rather than taken from "Core Max", which has the
    /// same value but does not say which core it is, and "Core Average" is never the hottest.</para>
    /// </remarks>
    private static void AddCpuReadings(string cpuName, List<ISensor> sensors, List<TemperatureReading> readings)
    {
        var chip = sensors.Where(s => !IsNotTheChipTemperature(s.Name)).ToList();

        // Take "CPU Package" or first available
        var packageSensor = chip.FirstOrDefault(s =>
            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) ?? chip.FirstOrDefault();

        if (packageSensor is not null)
        {
            readings.Add(new TemperatureReading("CPU", $"CPU Package ({cpuName})", packageSensor.Value));
        }

        // Add highest core temp if different from package
        var maxCore = chip
            .Where(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
            .MaxBy(s => s.Value);
        if (maxCore is not null && !ReferenceEquals(maxCore, packageSensor))
        {
            readings.Add(new TemperatureReading("CPU", $"Hottest Core ({maxCore.Name})", maxCore.Value));
        }
    }

    /// <summary>Whether a CPU sensor of type Temperature is something other than how hot the chip is.</summary>
    private static bool IsNotTheChipTemperature(string name) =>
        name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
        || name.Contains("(Tctl)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens LibreHardwareMonitor once, or returns null and gives up for the rest of the session.
    /// </summary>
    /// <remarks>
    /// <c>Open()</c> reads SMBIOS, builds the CPU, motherboard and storage groups, and loads LibreHardwareMonitor's
    /// modules into the PawnIO driver when it is installed. All of that is fault-prone under HVCI, Secure Boot, a
    /// locked driver or a WMI fault. The previous shape
    /// was <c>_computer ??= OpenComputer()</c> with no try/catch inside, so a failure left <c>_computer</c>
    /// null and the next Dashboard poll re-ran the heaviest possible init two seconds later — the driver work
    /// and a full hardware enumeration every two seconds for the life of the process, with one Debug
    /// line each time. The half-opened Computer was also dropped without <c>Close()</c>, abandoning whatever
    /// Open() had already claimed.
    /// <para>Same one-shot guard the NvAPI path below already uses (<c>_nvApiInitTried</c>) for exactly this
    /// failure mode.</para>
    /// </remarks>
    private static Computer? TryOpenComputer()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true
        };

        try
        {
            computer.Open();
            return computer;
        }
        catch (Exception ex)
        {
            // Broad on purpose: Open() reaches the PawnIO driver, SMBIOS and WMI, and the useful outcome is the
            // same whatever it throws — give up cleanly rather than retry all of it 30 times a minute.
            Log.Debug("LibreHardwareMonitor could not be opened, giving up for this session: {Error}", ex.Message);
            try { computer.Close(); }
            catch (Exception closeEx)
            {
                // Broad on purpose: the same native layer whose Open() has just failed.
                Log.Debug("LHM close after a failed open also failed: {Error}", closeEx.Message);
            }
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sensorLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); }
            catch (Exception ex)
            {
                // Broad on purpose: Close() releases what LibreHardwareMonitor opened, its PawnIO handles among them,
                // and disposing goes on whatever that throws.
                Log.Debug(ex, "LibreHardwareMonitor close failed");
            }
            _computer = null;
        }
        _enrichGate.Dispose();
    }

    private async Task EnrichStorageNamesAsync(List<TemperatureReading> readings)
    {
        try
        {
            // The friendly names come from DiskHealthService.CollectAsync()'s MSFT_PhysicalDisk +
            // per-disk MSFT_StorageReliabilityCounter (SMART) walk — the heaviest part of a read.
            // They are static hardware identity, so resolve once under the gate and cache; every
            // later call (the 2s Dashboard poll, the sampler, a user Refresh) returns the cached
            // list. The gate is taken unconditionally — an uncontended SemaphoreSlim acquire is
            // trivial next to the SMART walk it guards — which keeps the first resolution
            // race-safe without the double-checked re-test the previous shape needed.
            await _enrichGate.WaitAsync().ConfigureAwait(false);
            List<string> diskNames;
            try
            {
                diskNames = _cachedStorageFriendlyNames ??=
                    (await _diskHealth.CollectAsync().ConfigureAwait(false))
                    .Select(d => d.FriendlyName).ToList();
            }
            finally { _enrichGate.Release(); }

            ApplyStorageNames(readings, diskNames);
        }
        catch (Exception ex)
        {
            // Broad on purpose: the names are cosmetic, and failing to read them must not cost the readings they label.
            Log.Debug("Storage name enrichment failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Replaces placeholder storage names with the friendly names at the same position, when — and only
    /// when — that position means anything.
    /// </summary>
    /// <remarks>
    /// The two sequences are independently ordered. The readings are in LibreHardwareMonitor's device order;
    /// the names are in MSFT_PhysicalDisk enumeration order. Nothing correlates them, and the substitution
    /// used to be applied by index, unconditionally, over every storage reading — so a name LHM had reported
    /// correctly, or a WMI model the producer had already substituted for a placeholder, was overwritten with
    /// whatever name happened to sit at the same ordinal. A user then read a healthy NVMe's 38 °C under a
    /// failing HDD's name, or the reverse, which hides a genuinely hot drive.
    /// <para>Two guards, and each one alone is insufficient. A reading whose name LHM supplied is never
    /// touched, so a correct name cannot be clobbered. And an unequal count is proof the ordinal pairing is
    /// already broken — DiskHealthService.Collect silently SKIPS any disk with an unreadable WMI field, which
    /// shifts every later index by one — so in that case nothing is substituted at all. Keeping "Drive 2" is
    /// better than confidently showing the wrong model.</para>
    /// <para>Keying LHM's device identifier against MSFT_PhysicalDisk.DeviceId would remove the guessing
    /// entirely and is the better long-term shape. It needs a query change and a new property on
    /// DiskHealthReport, so it is deliberately not part of this change.</para>
    /// </remarks>
    internal static void ApplyStorageNames(List<TemperatureReading> readings, IReadOnlyList<string> diskNames)
    {
        var storageReadings = readings
            .Select((r, i) => (Reading: r, Index: i))
            .Where(x => x.Reading.Component == "Storage")
            .ToList();

        if (storageReadings.Count != diskNames.Count)
        {
            Log.Debug("Storage name enrichment skipped: {Readings} sensors vs {Names} disks — the ordinal "
                + "pairing would be a guess", storageReadings.Count, diskNames.Count);
            return;
        }

        for (var i = 0; i < storageReadings.Count; i++)
        {
            var (reading, idx) = storageReadings[i];

            if (!reading.NameIsPlaceholder) continue;
            if (string.IsNullOrWhiteSpace(diskNames[i])) continue;

            readings[idx] = reading with { SensorName = diskNames[i], NameIsPlaceholder = false };
        }
    }

    /// <summary>
    /// Disk models from Win32_DiskDrive, or null when WMI could not answer.
    /// </summary>
    /// <remarks>
    /// Null rather than empty on fault, so the caller's <c>??=</c> memoization does not cache a failure for
    /// the whole session. A single transient fault on the first elevated poll used to strand LibreHardwareMonitor's
    /// cryptic labels as "Drive 1", "Drive 2" until the app restarted.
    /// <para>COMException is caught alongside ManagementException because WMI surfaces transient faults — RPC
    /// server unavailable, repository errors — as COMException, which every other WMI reader in this codebase
    /// already guards. It mattered more here than elsewhere: this prefetch runs BEFORE the hardware
    /// enumeration loop, so an escaping COMException aborted the entire read through the outer catch-all and
    /// the Temperatures card lost its CPU, GPU and motherboard rows as well.</para>
    /// </remarks>
    private static List<string>? GetDiskNamesFromWmi()
    {
        List<string> names = [];
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model FROM Win32_DiskDrive ORDER BY Index");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var model = mo["Model"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(model))
                        names.Add(model);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Debug("WMI disk names failed: {Error}", ex.Message);
            // Whatever was collected before the fault is still correct and still in order; only a completely
            // empty result is worth retrying, so only that returns null.
            return names.Count > 0 ? names : null;
        }
        return names;
    }

    /// <summary>
    /// Adds a reading per NVIDIA GPU, through the app's one reader of that API.
    /// </summary>
    /// <remarks>
    /// This used to set NVIDIA's global API up itself, with its own init-once fields, as the Dashboard's 300 ms loop
    /// and the Resource History sampler's 10 s loop also did — three unsynchronised set-ups of a native API that is
    /// not documented as thread-safe, where a losing race switched one component's GPU reading off for the whole
    /// session (#2829). The reader owns the set-up, the handles and the lock over them.
    /// </remarks>
    private void ReadNvidiaGpuTemperatures(List<TemperatureReading> readings)
    {
        if (_disposed || _nvidia is null) return;

        foreach (var gpu in _nvidia.ReadTemperatures())
            readings.Add(new TemperatureReading("GPU", gpu.Name, gpu.TemperatureC));
    }

    /// <summary>How long a non-admin storage temperature read is reused before collecting again.</summary>
    /// <remarks>
    /// The elevated arm memoizes its SMART walk for the whole session because disk NAMES are static hardware
    /// identity. A temperature is not, so this arm gets a short time-to-live instead of a permanent cache.
    /// Thirty seconds against a 2-second poll cuts the work about fifteenfold and loses nothing observable:
    /// drive temperature moves on a minutes timescale.
    /// </remarks>
    internal static readonly TimeSpan DiskTemperatureTtl = TimeSpan.FromSeconds(30);

    private async Task ReadDiskTemperaturesAsync(List<TemperatureReading> readings)
    {
        // The Dashboard polls every 2 s with includeStorage: true, and on the NON-elevated path — the default
        // for most users — this used to call CollectAsync() cold every single tick: a fresh connect to
        // the Windows Storage WMI namespace, an MSFT_PhysicalDisk query, and one
        // MSFT_StorageReliabilityCounter association walk PER disk, thirty times a minute for as long as the
        // Dashboard is on screen. The service's own comments call that "by far the heaviest part of a read";
        // the memoization that followed was applied to the elevated arm only.
        await _enrichGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // GetElapsedTime over a stored GetTimestamp, not a wall-clock difference. EtwBandwidthSource
            // spells out why: GetUtcNow moves when the user or NTP changes the clock, which would either
            // freeze this cache or expire it instantly.
            if (_cachedDiskTemperatures is not null
                && _timeProvider.GetElapsedTime(_diskTemperaturesStamp) <= DiskTemperatureTtl)
            {
                readings.AddRange(_cachedDiskTemperatures);
                return;
            }

            List<TemperatureReading> fresh = [];
            try
            {
                var disks = await _diskHealth.CollectAsync().ConfigureAwait(false);
                foreach (var disk in disks.Where(d => d.TemperatureC.HasValue))
                {
                    fresh.Add(new TemperatureReading("Storage", disk.FriendlyName, disk.TemperatureC));
                }
            }
            catch (ManagementException ex) { Log.Debug("Disk temp unavailable: {Error}", ex.Message); return; }
            catch (UnauthorizedAccessException ex) { Log.Debug("Disk temp denied: {Error}", ex.Message); return; }
            catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("Disk temp WMI COM error: 0x{HResult:X8}", ex.HResult); return; }

            // Only a successful collect is cached. Caching a failure would hide the drives for the next
            // 30 seconds instead of retrying on the next tick — the same mistake the WMI name query made by
            // memoizing an empty result for the whole session.
            _cachedDiskTemperatures = fresh;
            _diskTemperaturesStamp = _timeProvider.GetTimestamp();
            readings.AddRange(fresh);
        }
        finally { _enrichGate.Release(); }
    }
}
