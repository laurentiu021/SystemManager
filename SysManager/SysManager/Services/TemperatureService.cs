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
/// With admin: LibreHardwareMonitor gives ALL temps (CPU, GPU, Disk, Motherboard).
/// Without admin: NVIDIA GPU (via NvAPIWrapper) + Disk SMART temps only.
/// </summary>
public sealed class TemperatureService : IDisposable
{
    private readonly DiskHealthService _diskHealth;
    private readonly bool _skipHardwareInit;

    // LibreHardwareMonitor's Computer.Open() loads a ring0 kernel driver and
    // enumerates all hardware — far too heavy to do on every 2s poll. Open it once
    // (lazily, on the first elevated read) and keep it alive for the service
    // lifetime; each poll then only calls Update(). The lock serialises the
    // not-thread-safe native sensor access — both the LibreHardwareMonitor path
    // (admin) and the NvAPI path (non-admin, incl. its init-once fields) — in case
    // more than one caller polls at once (the 2s temperature poll, the user's
    // Refresh command, and the always-on 10s resource sampler all share this service).
    private readonly Lock _sensorLock = new();
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

    /// <param name="diskHealth">Source of the SMART/temperature walk.</param>
    /// <param name="skipHardwareInit">Set by tests so no kernel driver is loaded.</param>
    /// <param name="timeProvider">
    /// Clock for the storage-temperature time-to-live. Optional with a System default, like
    /// <paramref name="skipHardwareInit"/>, so the ten existing construction sites are unaffected while a test
    /// can still drive the TTL without sleeping.
    /// </param>
    public TemperatureService(DiskHealthService diskHealth, bool skipHardwareInit = false,
        TimeProvider? timeProvider = null)
    {
        _diskHealth = diskHealth;
        _skipHardwareInit = skipHardwareInit;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            readings.Add(new TemperatureReading("CPU", "CPU Package", null, RequiresAdmin: true));
        }

        return readings;
    }

    private void ReadViaLibreHardwareMonitor(List<TemperatureReading> readings, bool includeStorage = true)
    {
        lock (_sensorLock)
        {
            if (_disposed) return;
            try
            {
                // Open the kernel-level monitor once and reuse it; subsequent polls
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

                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();

                    foreach (var subHardware in hardware.SubHardware)
                        subHardware.Update();

                    // Topology once per session, not once per hardware item per 2s poll — see
                    // _loggedSensorTopology. The diagnostic value is "what sensors does this machine
                    // expose", which is answered by the first read and unchanged by the 30th.
                    if (!_loggedSensorTopology)
                    {
                        Log.Debug("LHM: {Type} '{Name}' — {SensorCount} temp sensors",
                            hardware.HardwareType, hardware.Name,
                            hardware.Sensors.Count(s => s.SensorType == SensorType.Temperature));
                    }

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
                        // Take "CPU Package" or first available
                        var packageSensor = tempSensors.FirstOrDefault(s =>
                            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) ?? tempSensors.FirstOrDefault();

                        if (packageSensor is not null)
                        {
                            readings.Add(new TemperatureReading("CPU", $"CPU Package ({hardware.Name})",
                                packageSensor.Value));
                        }

                        // Add highest core temp if different from package
                        var maxCore = tempSensors
                            .Where(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                            .MaxBy(s => s.Value);
                        if (maxCore is not null && !ReferenceEquals(maxCore, packageSensor))
                        {
                            readings.Add(new TemperatureReading("CPU", $"Hottest Core ({maxCore.Name})",
                                maxCore.Value));
                        }
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
                        if (!includeStorage) continue;
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
            catch (Exception ex)
            {
                Log.Debug("LibreHardwareMonitor failed: {Error}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Opens LibreHardwareMonitor once, or returns null and gives up for the rest of the session.
    /// </summary>
    /// <remarks>
    /// <c>Open()</c> loads a ring0 driver, reads SMBIOS and builds the CPU, motherboard and storage groups.
    /// All of that is fault-prone under HVCI, Secure Boot, a locked driver or a WMI fault. The previous shape
    /// was <c>_computer ??= OpenComputer()</c> with no try/catch inside, so a failure left <c>_computer</c>
    /// null and the next Dashboard poll re-ran the heaviest possible init two seconds later — a kernel-driver
    /// load and a full hardware enumeration every two seconds for the life of the process, with one Debug
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
            // Broad on purpose: Open() reaches a kernel driver, SMBIOS and WMI, and the useful outcome is the
            // same whatever it throws — give up cleanly rather than retry a driver load 30 times a minute.
            Log.Debug("LibreHardwareMonitor could not be opened, giving up for this session: {Error}", ex.Message);
            try { computer.Close(); }
            catch (Exception closeEx) { Log.Debug("LHM close after a failed open also failed: {Error}", closeEx.Message); }
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
            catch (Exception ex) { Log.Debug(ex, "LibreHardwareMonitor close failed"); }
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
            Log.Debug("Storage name enrichment failed: {Error}", ex.Message);
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

    // NvAPI init is one-time. Re-running NVIDIA.Initialize() + GetPhysicalGPUs() on every poll
    // re-does the setup and, on a non-NVIDIA machine, throws (and swallows) an exception on every
    // tick — the temperature poll runs about every 2 s. We initialize at most once, remember
    // whether any NVIDIA GPU is present, and skip the whole read thereafter when none is.
    private bool _nvApiInitTried;
    private bool _nvApiAvailable;

    private void ReadNvidiaGpuTemperatures(List<TemperatureReading> readings)
    {
        // Serialise under the same lock as the LHM path: NVIDIA.Initialize()/GetPhysicalGPUs()
        // is a global native call that isn't documented thread-safe, and the init-once fields
        // below are shared, non-volatile state. Concurrent non-admin callers (2s poll + user
        // Refresh + 10s sampler) could otherwise both init at once and a losing thread could
        // latch _nvApiAvailable=false permanently, silently dropping GPU temps for the session.
        lock (_sensorLock)
        {
            if (_disposed) return;

            if (!_nvApiInitTried)
            {
                _nvApiInitTried = true;
                try
                {
                    NvAPIWrapper.NVIDIA.Initialize();
                    _nvApiAvailable = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs().Length > 0;
                }
                // No NVIDIA GPU / driver present is the normal case on AMD/Intel systems — record it
                // once so later polls skip silently instead of throwing an exception every tick.
                catch (NvAPIWrapper.Native.Exceptions.NVIDIAApiException) { _nvApiAvailable = false; }
                catch (DllNotFoundException) { _nvApiAvailable = false; }
                catch (Exception ex) when (ex is TypeInitializationException or InvalidOperationException)
                {
                    _nvApiAvailable = false;
                    Log.Debug("NVIDIA API init failed: {Error}", ex.Message);
                }
            }

            if (!_nvApiAvailable) return;

            try
            {
                foreach (var gpu in NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs())
                {
                    var sensor = gpu.ThermalInformation.ThermalSensors
                        .FirstOrDefault(s => s.CurrentTemperature > 0);
                    if (sensor is not null)
                    {
                        readings.Add(new TemperatureReading("GPU", gpu.FullName,
                            sensor.CurrentTemperature));
                    }
                }
            }
            // A transient read failure (e.g. a driver reset) must not crash the poll — skip this tick.
            catch (NvAPIWrapper.Native.Exceptions.NVIDIAApiException) { /* transient GPU read failure */ }
            catch (Exception ex) when (ex is TypeInitializationException or InvalidOperationException)
            {
                Log.Debug("NVIDIA GPU temperature read failed: {Error}", ex.Message);
            }
        }
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
