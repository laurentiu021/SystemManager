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

    public TemperatureService(DiskHealthService diskHealth, bool skipHardwareInit = false)
    {
        _diskHealth = diskHealth;
        _skipHardwareInit = skipHardwareInit;
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
                // only Update() the already-enumerated hardware.
                _computer ??= OpenComputer();

                // Pre-fetch disk names from WMI for cross-reference (skipped on the fast path —
                // the sampler doesn't need names). Memoized: disk models are static hardware, so
                // resolve the Win32_DiskDrive query once and reuse it — the 2s poll no longer
                // re-queries WMI every tick. Guarded by _sensorLock (already held here).
                var diskNames = includeStorage ? (_cachedWmiDiskNames ??= GetDiskNamesFromWmi()) : [];

                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();

                    foreach (var subHardware in hardware.SubHardware)
                        subHardware.Update();

                    Log.Debug("LHM: {Type} '{Name}' — {SensorCount} temp sensors",
                        hardware.HardwareType, hardware.Name,
                        hardware.Sensors.Count(s => s.SensorType == SensorType.Temperature));

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
                            if (string.IsNullOrWhiteSpace(name) || name.Length <= 3 || name.All(char.IsDigit))
                            {
                                // Try matching by index from WMI disk list
                                var storageIndex = readings.Count(r => r.Component == "Storage");
                                name = storageIndex < diskNames.Count
                                    ? diskNames[storageIndex]
                                    : $"Drive {storageIndex + 1}";
                            }

                            readings.Add(new TemperatureReading("Storage", name, diskTemp.Value));
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
            }
            catch (Exception ex)
            {
                Log.Debug("LibreHardwareMonitor failed: {Error}", ex.Message);
            }
        }
    }

    private static Computer OpenComputer()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true
        };
        computer.Open();
        return computer;
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
            // They are static hardware identity, so resolve once and cache; the 2s Dashboard poll
            // then skips the SMART walk entirely on every tick after the first. The gate makes the
            // first-resolution race-safe when the poll, the user Refresh, and the sampler overlap.
            var diskNames = _cachedStorageFriendlyNames;
            if (diskNames is null)
            {
                await _enrichGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_cachedStorageFriendlyNames is null)
                    {
                        var disks = await _diskHealth.CollectAsync().ConfigureAwait(false);
                        _cachedStorageFriendlyNames = disks.Select(d => d.FriendlyName).ToList();
                    }
                    diskNames = _cachedStorageFriendlyNames;
                }
                finally { _enrichGate.Release(); }
            }

            var storageReadings = readings
                .Select((r, i) => (Reading: r, Index: i))
                .Where(x => x.Reading.Component == "Storage")
                .ToList();

            for (int i = 0; i < storageReadings.Count; i++)
            {
                var (reading, idx) = storageReadings[i];
                if (i < diskNames.Count && !string.IsNullOrWhiteSpace(diskNames[i]))
                {
                    readings[idx] = reading with { SensorName = diskNames[i] };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Storage name enrichment failed: {Error}", ex.Message);
        }
    }

    private static List<string> GetDiskNamesFromWmi()
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
        catch (ManagementException ex)
        {
            Log.Debug("WMI disk names failed: {Error}", ex.Message);
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

    private async Task ReadDiskTemperaturesAsync(List<TemperatureReading> readings)
    {
        try
        {
            var disks = await _diskHealth.CollectAsync();
            foreach (var disk in disks.Where(d => d.TemperatureC.HasValue))
            {
                readings.Add(new TemperatureReading("Storage", disk.FriendlyName, disk.TemperatureC));
            }
        }
        catch (ManagementException ex) { Log.Debug("Disk temp unavailable: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Disk temp denied: {Error}", ex.Message); }
    }
}
