// SysManager · SystemReportService — generates a comprehensive plain-text system report
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using System.Text;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Generates a full system report combining existing service data (OS, CPU, RAM,
/// Disks, SMART) with additional WMI queries (GPU, Motherboard, Network adapters).
/// Output is a plain-text document suitable for clipboard or file export.
/// </summary>
public sealed class SystemReportService
{
    private readonly SystemInfoService _sysInfo;
    private readonly DiskHealthService _diskHealth;

    public SystemReportService(SystemInfoService sysInfo, DiskHealthService diskHealth)
    {
        _sysInfo = sysInfo;
        _diskHealth = diskHealth;
    }

    /// <summary>
    /// Generates a formatted plain-text system report.
    /// </summary>
    public async Task<string> GenerateReportAsync(CancellationToken ct = default)
    {
        var snapshot = await _sysInfo.CaptureAsync(ct).ConfigureAwait(false);
        var diskHealth = await _diskHealth.CollectAsync(ct).ConfigureAwait(false);

        return await Task.Run(() => BuildReport(snapshot, diskHealth), ct);
    }

    private static string BuildReport(SystemSnapshot snapshot, IReadOnlyList<DiskHealthReport> diskHealth)
    {
        var sb = new StringBuilder(4096);
        var now = DateTime.Now;

        // Header
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("  SysManager System Report");
        sb.AppendLine($"  Generated: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine();

        // Operating System
        AppendSection(sb, "Operating System");
        sb.AppendLine($"  {snapshot.Os.Caption}");
        if (!string.IsNullOrWhiteSpace(snapshot.Os.Version))
            sb.AppendLine($"  Version: {snapshot.Os.Version}");
        if (!string.IsNullOrWhiteSpace(snapshot.Os.BuildNumber))
            sb.AppendLine($"  Build: {snapshot.Os.BuildNumber}");
        if (!string.IsNullOrWhiteSpace(snapshot.Os.Architecture))
            sb.AppendLine($"  Architecture: {snapshot.Os.Architecture}");
        if (snapshot.Os.Uptime > TimeSpan.Zero)
            sb.AppendLine($"  Uptime: {(int)snapshot.Os.Uptime.TotalDays} days, {snapshot.Os.Uptime.Hours} hours");
        sb.AppendLine();

        // CPU
        AppendSection(sb, "CPU");
        sb.Append($"  {snapshot.Cpu.Name}");
        if (snapshot.Cpu.Cores > 0)
            sb.Append($" ({snapshot.Cpu.Cores} cores / {snapshot.Cpu.LogicalProcessors} threads)");
        sb.AppendLine();
        if (snapshot.Cpu.MaxClockMHz > 0)
            sb.AppendLine($"  Base: {snapshot.Cpu.MaxClockMHz / 1000.0:F1} GHz");
        sb.AppendLine();

        // Memory
        AppendSection(sb, "Memory");
        sb.AppendLine($"  {snapshot.Memory.TotalGB:F1} GB total");
        sb.AppendLine($"  Used: {snapshot.Memory.UsedGB:F1} / {snapshot.Memory.TotalGB:F1} GB ({snapshot.Memory.UsedPercent:F0}%)");
        if (snapshot.Memory.Modules.Count > 0)
        {
            sb.AppendLine("  Slots:");
            foreach (var mod in snapshot.Memory.Modules)
            {
                sb.Append($"    {mod.BankLabel}: {mod.CapacityGB:F0} GB");
                if (!string.IsNullOrWhiteSpace(mod.Manufacturer))
                    sb.Append($" {mod.Manufacturer}");
                if (mod.SpeedMHz > 0)
                    sb.Append($" {mod.SpeedMHz} MHz");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // GPU
        AppendSection(sb, "GPU");
        AppendGpuInfo(sb);
        sb.AppendLine();

        // Motherboard
        AppendSection(sb, "Motherboard");
        AppendMotherboardInfo(sb);
        sb.AppendLine();

        // Storage
        AppendSection(sb, "Storage");
        if (diskHealth.Count > 0)
        {
            foreach (var disk in diskHealth)
            {
                sb.Append($"  {disk.FriendlyName}");
                if (!string.IsNullOrWhiteSpace(disk.MediaType) && disk.MediaType != "Unspecified")
                    sb.Append($" — {disk.MediaType}");
                if (!string.IsNullOrWhiteSpace(disk.BusType) && disk.BusType != "Other")
                    sb.Append($" ({disk.BusType})");
                if (disk.SizeGB > 0)
                    sb.Append($" — {disk.SizeGB:F0} GB");
                sb.Append($" — {disk.HealthStatus}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(disk.Verdict))
                    sb.AppendLine($"    {disk.Verdict}");
                if (disk.TemperatureC.HasValue)
                    sb.AppendLine($"    Temperature: {disk.TemperatureC:F0} °C");
                if (disk.WearPercent.HasValue)
                    sb.AppendLine($"    Wear: {disk.WearPercent}%");
                if (disk.PowerOnHours.HasValue)
                    sb.AppendLine($"    Power-on: {disk.PowerOnDisplay}");
            }
        }
        else if (snapshot.Disks.Count > 0)
        {
            foreach (var disk in snapshot.Disks)
            {
                sb.Append($"  {disk.FriendlyName}");
                if (!string.IsNullOrWhiteSpace(disk.MediaType) && disk.MediaType != "Unspecified")
                    sb.Append($" — {disk.MediaType}");
                if (!string.IsNullOrWhiteSpace(disk.BusType) && disk.BusType != "Other")
                    sb.Append($" ({disk.BusType})");
                if (disk.SizeGB > 0)
                    sb.Append($" — {disk.SizeGB:F0} GB");
                sb.Append($" — {disk.HealthStatus}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("  (No disk information available)");
        }
        sb.AppendLine();

        // Network
        AppendSection(sb, "Network");
        AppendNetworkInfo(sb);
        sb.AppendLine();

        // Footer
        sb.AppendLine("───────────────────────────────────────────");
        sb.AppendLine($"  End of report. Generated by SysManager v{UpdateService.CurrentVersion.ToString(3)}");

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title)
    {
        sb.Append("── ");
        sb.Append(title);
        sb.Append(' ');
        var remaining = Math.Max(0, 42 - title.Length - 4);
        sb.Append('─', remaining);
        sb.AppendLine();
    }

    private static void AppendGpuInfo(StringBuilder sb)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            using var collection = searcher.Get();
            bool found = false;
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    found = true;
                    var name = mo["Name"]?.ToString()?.Trim() ?? "Unknown GPU";
                    sb.Append($"  {name}");

                    var adapterRam = mo["AdapterRAM"];
                    if (adapterRam != null)
                    {
                        var ramBytes = Convert.ToUInt64(adapterRam);
                        if (ramBytes > 0)
                            sb.Append($" — VRAM: {ramBytes / 1024.0 / 1024.0 / 1024.0:F1} GB");
                    }

                    var driver = mo["DriverVersion"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(driver))
                        sb.Append($" — Driver: {driver}");

                    sb.AppendLine();
                }
            }
            if (!found)
                sb.AppendLine("  (No GPU information available)");
        }
        catch (ManagementException ex)
        {
            Log.Debug("GPU info unavailable for report: {Error}", ex.Message);
            sb.AppendLine("  (GPU information unavailable)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            sb.AppendLine("  (GPU information unavailable)");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("GPU info access denied: {Error}", ex.Message);
            sb.AppendLine("  (GPU information unavailable — access denied)");
        }
    }

    private static void AppendMotherboardInfo(StringBuilder sb)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var collection = searcher.Get();
            bool found = false;
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    found = true;
                    var manufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var product = mo["Product"]?.ToString()?.Trim() ?? "";

                    if (!string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(product))
                        sb.AppendLine($"  {manufacturer} {product}".TrimEnd());
                    else
                        sb.AppendLine("  (Unknown motherboard)");
                }
            }
            if (!found)
                sb.AppendLine("  (No motherboard information available)");
        }
        catch (ManagementException ex)
        {
            Log.Debug("Motherboard info unavailable for report: {Error}", ex.Message);
            sb.AppendLine("  (Motherboard information unavailable)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            sb.AppendLine("  (Motherboard information unavailable)");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Motherboard info access denied: {Error}", ex.Message);
            sb.AppendLine("  (Motherboard information unavailable — access denied)");
        }
    }

    private static void AppendNetworkInfo(StringBuilder sb)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, IPAddress, MACAddress, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            using var collection = searcher.Get();
            bool found = false;
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    found = true;
                    var desc = mo["Description"]?.ToString()?.Trim() ?? "Unknown adapter";
                    var mac = mo["MACAddress"]?.ToString() ?? "";
                    var dhcp = mo["DHCPEnabled"] is true ? "DHCP" : "Static";

                    sb.Append($"  {desc}");

                    // IPAddress is a string array
                    if (mo["IPAddress"] is string[] addresses && addresses.Length > 0)
                    {
                        var ipv4 = addresses.FirstOrDefault(a => !a.Contains(':'));
                        if (!string.IsNullOrWhiteSpace(ipv4))
                            sb.Append($" — {ipv4}");
                    }

                    sb.Append($" — MAC: {mac}");
                    sb.Append($" — {dhcp}");
                    sb.AppendLine();
                }
            }
            if (!found)
                sb.AppendLine("  (No active network adapters found)");
        }
        catch (ManagementException ex)
        {
            Log.Debug("Network info unavailable for report: {Error}", ex.Message);
            sb.AppendLine("  (Network information unavailable)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            sb.AppendLine("  (Network information unavailable)");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Network info access denied: {Error}", ex.Message);
            sb.AppendLine("  (Network information unavailable — access denied)");
        }
    }
}
