// SysManager · DriverEntry — model for installed drivers
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Represents an installed Windows driver from Win32_PnPSignedDriver.
/// </summary>
public sealed class DriverEntry
{
    public string DeviceName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }

    /// <summary>Formatted date for display (yyyy-MM-dd or empty).</summary>
    public string DriverDateDisplay => DriverDate?.ToString("yyyy-MM-dd") ?? "";
}
