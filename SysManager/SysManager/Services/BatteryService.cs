// SysManager · BatteryService — reads battery health via WMI
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Queries Win32_Battery and BatteryStaticData / BatteryFullChargedCapacity
/// from WMI to build a <see cref="BatteryInfo"/> snapshot.
/// </summary>
public sealed class BatteryService
{
    /// <summary>The battery as Windows reports it, or null when Windows could not be asked.</summary>
    public Task<BatteryInfo?> GetBatteryInfoAsync(CancellationToken ct = default)
        => Task.Run(() => GetBatteryInfo(), ct);

    /// <summary>
    /// The battery as Windows reports it, or null when the <c>Win32_Battery</c> query failed. A PC without a
    /// battery is a <see cref="BatteryInfo"/> whose <see cref="BatteryInfo.HasBattery"/> is false, not null.
    /// </summary>
    /// <remarks>
    /// A failed query used to fall through to "No battery detected", so a laptop whose WMI did not answer was
    /// told it runs on AC power only (#2503). The three <c>root\WMI</c> reads after it keep their quiet
    /// fallbacks: a standard user cannot read those classes, which leaves the health unmeasured and shown as
    /// such rather than claimed.
    /// </remarks>
    internal static BatteryInfo? GetBatteryInfo() => GetBatteryInfo(ReadWin32Battery);

    /// <summary><see cref="GetBatteryInfo()"/> handed the <c>Win32_Battery</c> read, so its failures are testable.</summary>
    internal static BatteryInfo? GetBatteryInfo(Func<BatteryInfo> readBattery)
    {
        BatteryInfo info;
        try
        {
            info = readBattery();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Warning("Battery query failed: {Error}", ex.Message);
            return null;
        }

        if (!info.HasBattery)
        {
            info.Status = "No battery detected";
            return info;
        }

        ReadCapacities(info);
        return info;
    }

    /// <summary>The first battery <c>Win32_Battery</c> lists, or one with <see cref="BatteryInfo.HasBattery"/> false.</summary>
    private static BatteryInfo ReadWin32Battery()
    {
        var info = new BatteryInfo();

        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
        using var results = searcher.Get();

        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                info.HasBattery = true;
                info.Name = obj["Name"]?.ToString() ?? "";
                info.Manufacturer = obj["DeviceID"]?.ToString() ?? "";
                info.ChargePercent = ToInt32Safe(obj["EstimatedChargeRemaining"]);
                info.Chemistry = MapChemistry(ToUInt16Safe(obj["Chemistry"]));

                var statusCode = ToUInt16Safe(obj["BatteryStatus"]);
                info.Status = MapBatteryStatus(statusCode);

                var runtime = ToInt64Safe(obj["EstimatedRunTime"]);
                info.EstimatedRuntimeMinutes = runtime >= 71_582_788 ? -1 : (int)runtime;

                break; // first battery only
            }
        }

        return info;
    }

    /// <summary>Design capacity, full-charge capacity and cycle count, each left unset when it cannot be read.</summary>
    private static void ReadCapacities(BatteryInfo info)
    {

        // ── BatteryStaticData (design capacity) ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    info.DesignCapacityMWh = ToUInt32Safe(obj["DesignedCapacity"]);
                    break;
                }
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }
        catch (System.Runtime.InteropServices.COMException) { /* transient WMI COM fault — return what we have */ }

        // ── BatteryFullChargedCapacity (current max) ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    info.FullChargeCapacityMWh = ToUInt32Safe(obj["FullChargedCapacity"]);
                    break;
                }
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }
        catch (System.Runtime.InteropServices.COMException) { /* transient WMI COM fault — return what we have */ }

        // ── BatteryCycleCount ──
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    info.CycleCount = ToInt32Safe(obj["CycleCount"]);
                    break;
                }
            }
        }
        catch (ManagementException) { /* WMI class not present on this device */ }
        catch (UnauthorizedAccessException) { /* needs elevation for root\WMI */ }
        catch (System.Runtime.InteropServices.COMException) { /* transient WMI COM fault — return what we have */ }
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

    // Safe WMI-value converters: a malformed/unexpected value would make Convert.To*
    // throw InvalidCastException/FormatException/OverflowException, which the WMI
    // try-blocks above don't catch — falling back to a default keeps the scan resilient.
    private static int ToInt32Safe(object? value, int fallback = 0)
    {
        try { return Convert.ToInt32(value ?? fallback); }
        catch (InvalidCastException) { return fallback; }
        catch (FormatException) { return fallback; }
        catch (OverflowException) { return fallback; }
    }

    private static ushort ToUInt16Safe(object? value, ushort fallback = 0)
    {
        try { return Convert.ToUInt16(value ?? fallback); }
        catch (InvalidCastException) { return fallback; }
        catch (FormatException) { return fallback; }
        catch (OverflowException) { return fallback; }
    }

    private static uint ToUInt32Safe(object? value, uint fallback = 0)
    {
        try { return Convert.ToUInt32(value ?? fallback); }
        catch (InvalidCastException) { return fallback; }
        catch (FormatException) { return fallback; }
        catch (OverflowException) { return fallback; }
    }

    private static long ToInt64Safe(object? value, long fallback = 0)
    {
        try { return Convert.ToInt64(value ?? fallback); }
        catch (InvalidCastException) { return fallback; }
        catch (FormatException) { return fallback; }
        catch (OverflowException) { return fallback; }
    }
}
