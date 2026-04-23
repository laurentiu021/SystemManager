// SysManager · BatteryService — reads battery health via WMI
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Management;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Queries Win32_Battery and BatteryStaticData / BatteryFullChargedCapacity
/// from WMI to build a <see cref="BatteryInfo"/> snapshot.
/// </summary>
public sealed class BatteryService
{
    public Task<BatteryInfo> GetBatteryInfoAsync(CancellationToken ct = default)
        => Task.Run(() => GetBatteryInfo(), ct);

    internal static BatteryInfo GetBatteryInfo()
    {
        var info = new BatteryInfo();

        // ── Win32_Battery (basic info) ──
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                info.HasBattery = true;
                info.Name = obj["Name"]?.ToString() ?? "";
                info.Manufacturer = obj["DeviceID"]?.ToString() ?? "";
                info.ChargePercent = Convert.ToInt32(obj["EstimatedChargeRemaining"] ?? 0);
                info.Chemistry = MapChemistry(Convert.ToUInt16(obj["Chemistry"] ?? 0));

                var statusCode = Convert.ToUInt16(obj["BatteryStatus"] ?? 0);
                info.Status = MapBatteryStatus(statusCode);

                var runtime = Convert.ToInt64(obj["EstimatedRunTime"] ?? 0);
                info.EstimatedRuntimeMinutes = runtime >= 71_582_788 ? -1 : (int)runtime;

                break; // first battery only
            }
        }
        catch (ManagementException) { /* WMI class not available */ }
        catch (UnauthorizedAccessException) { /* insufficient permissions */ }

        if (!info.HasBattery)
        {
            info.Status = "No battery detected";
            return info;
        }

        // ── BatteryStaticData (design capacity) ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                info.DesignCapacityMWh = Convert.ToUInt32(obj["DesignedCapacity"] ?? 0);
                break;
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }

        // ── BatteryFullChargedCapacity (current max) ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                info.FullChargeCapacityMWh = Convert.ToUInt32(obj["FullChargedCapacity"] ?? 0);
                break;
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }

        // ── BatteryCycleCount ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                info.CycleCount = Convert.ToInt32(obj["CycleCount"] ?? 0);
                break;
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }

        return info;
    }

    internal static string MapBatteryStatus(ushort code) => code switch
    {
        1 => "Discharging",
        2 => "AC power",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging (high)",
        8 => "Charging (low)",
        9 => "Charging (critical)",
        10 => "Undefined",
        11 => "Partially charged",
        _ => "Unknown"
    };

    internal static string MapChemistry(ushort code) => code switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "Lead Acid",
        4 => "Nickel Cadmium",
        5 => "Nickel Metal Hydride",
        6 => "Lithium-ion",
        7 => "Zinc Air",
        8 => "Lithium Polymer",
        _ => "Unknown"
    };
}
