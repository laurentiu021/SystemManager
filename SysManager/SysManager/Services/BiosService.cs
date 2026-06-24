// SysManager · BiosService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads BIOS/firmware and motherboard information (Win32_BIOS, Win32_BaseBoard) and
/// resolves the right manufacturer support page for BIOS updates. Strictly read-only —
/// it never attempts to flash firmware. The support-URL resolver is a pure static method
/// so it can be unit-tested without any WMI access.
/// </summary>
public sealed class BiosService
{
    /// <summary>Gathers BIOS + motherboard info. Returns an empty-ish record if queries fail.</summary>
    public BiosInfo Read()
    {
        string version = "", releaseDate = "", manufacturer = "";
        string boardManufacturer = "", boardProduct = "";

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate FROM Win32_BIOS");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    version = mo["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    manufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                    releaseDate = FormatWmiDate(mo["ReleaseDate"]?.ToString());
                    break;
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("BIOS info unavailable: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("BIOS info WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("BIOS info access denied: {Error}", ex.Message); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    boardManufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                    boardProduct = mo["Product"]?.ToString()?.Trim() ?? "";
                    break;
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("Motherboard info unavailable: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("Motherboard info WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Motherboard info access denied: {Error}", ex.Message); }

        return new BiosInfo(version, releaseDate, manufacturer, ReadBootMode(), ReadSecureBoot(), boardManufacturer, boardProduct);
    }

    /// <summary>UEFI vs Legacy via the PEFirmwareType registry value (1=BIOS, 2=UEFI).</summary>
    private static string ReadBootMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return key?.GetValue("PEFirmwareType") switch
            {
                2 => "UEFI",
                1 => "Legacy BIOS",
                _ => "Unknown"
            };
        }
        catch (System.Security.SecurityException) { return "Unknown"; }
        catch (UnauthorizedAccessException) { return "Unknown"; }
    }

    /// <summary>Secure Boot on/off via the SecureBoot\State\UEFISecureBootEnabled registry value.</summary>
    private static string ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") switch
            {
                1 => "On",
                0 => "Off",
                _ => "Unknown"
            };
        }
        catch (System.Security.SecurityException) { return "Unknown"; }
        catch (UnauthorizedAccessException) { return "Unknown"; }
    }

    /// <summary>Converts a WMI CIM_DATETIME (yyyymmdd...) to yyyy-MM-dd, or "" if unparseable.</summary>
    internal static string FormatWmiDate(string? cimDate)
    {
        if (string.IsNullOrWhiteSpace(cimDate) || cimDate.Length < 8) return "";
        var datePart = cimDate[..8];
        return long.TryParse(datePart, out _)
            ? $"{datePart[..4]}-{datePart[4..6]}-{datePart[6..8]}"
            : "";
    }

    /// <summary>
    /// Resolves the manufacturer's BIOS-update support page from the motherboard/system
    /// manufacturer. Falls back to a web search for the board model. Pure + unit-tested.
    /// </summary>
    public static string SupportUrl(string manufacturer, string boardModel)
    {
        var m = (manufacturer ?? "").ToLowerInvariant();
        if (m.Contains("asus")) return "https://www.asus.com/support/";
        if (m.Contains("msi") || m.Contains("micro-star")) return "https://www.msi.com/support";
        if (m.Contains("gigabyte")) return "https://www.gigabyte.com/Support";
        if (m.Contains("asrock")) return "https://www.asrock.com/support/";
        if (m.Contains("dell")) return "https://www.dell.com/support/home";
        if (m.Contains("hewlett") || m.Contains("hp")) return "https://support.hp.com";
        if (m.Contains("lenovo")) return "https://pcsupport.lenovo.com";
        if (m.Contains("acer")) return "https://www.acer.com/support";
        if (m.Contains("biostar")) return "https://www.biostar.com.tw/app/en/support/";

        var query = Uri.EscapeDataString($"{manufacturer} {boardModel} BIOS update".Trim());
        return $"https://www.google.com/search?q={query}";
    }
}
