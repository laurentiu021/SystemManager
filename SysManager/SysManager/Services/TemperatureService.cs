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
public sealed class TemperatureService
{
    private readonly DiskHealthService _diskHealth;
    private readonly bool _skipHardwareInit;

    public TemperatureService(DiskHealthService diskHealth, bool skipHardwareInit = false)
    {
        _diskHealth = diskHealth;
        _skipHardwareInit = skipHardwareInit;
    }

    public async Task<List<TemperatureReading>> ReadAllAsync()
    {
        if (_skipHardwareInit) return [];

        List<TemperatureReading> readings = [];
        var isAdmin = AdminHelper.IsElevated();

        if (isAdmin)
        {
            await Task.Run(() => ReadViaLibreHardwareMonitor(readings));

            // LHM storage often has bad names — enrich from DiskHealthService
            await EnrichStorageNamesAsync(readings);
        }
        else
        {
            await Task.Run(() => ReadNvidiaGpuTemperatures(readings));
            await ReadDiskTemperaturesAsync(readings);

            readings.Add(new TemperatureReading("CPU", "CPU Package", null, RequiresAdmin: true));
        }

        return readings;
    }

    private static void ReadViaLibreHardwareMonitor(List<TemperatureReading> readings)
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true
            };
            computer.Open();

            // Pre-fetch disk names from WMI for cross-reference
            var diskNames = GetDiskNamesFromWmi();

            foreach (var hardware in computer.Hardware)
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
        finally
        {
            try { computer?.Close(); }
            catch (Exception ex) { Log.Debug(ex, "LibreHardwareMonitor close failed"); }
        }
    }

    private async Task EnrichStorageNamesAsync(List<TemperatureReading> readings)
    {
        try
        {
            var disks = await _diskHealth.CollectAsync();
            var diskNames = disks.Select(d => d.FriendlyName).ToList();

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

    private static void ReadNvidiaGpuTemperatures(List<TemperatureReading> readings)
    {
        try
        {
            NvAPIWrapper.NVIDIA.Initialize();
            var gpus = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs();

            foreach (var gpu in gpus)
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
        // No NVIDIA GPU / driver present is the normal case on AMD/Intel systems —
        // skip GPU temperatures silently rather than logging noise on every poll.
        catch (NvAPIWrapper.Native.Exceptions.NVIDIAApiException) { /* no NVIDIA GPU */ }
        catch (DllNotFoundException) { /* nvapi.dll not installed */ }
        catch (Exception ex) when (ex is TypeInitializationException or InvalidOperationException)
        {
            Log.Debug("NVIDIA API init failed: {Error}", ex.Message);
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
