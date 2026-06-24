// SysManager · BiosInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Read-only BIOS / firmware and motherboard information for the System Health tab.
/// Gathered from Win32_BIOS and Win32_BaseBoard plus UEFI/Secure Boot detection.
/// </summary>
public sealed record BiosInfo(
    string Version,
    string ReleaseDate,
    string Manufacturer,
    string BootMode,        // "UEFI" / "Legacy BIOS" / "Unknown"
    string SecureBoot,      // "On" / "Off" / "Unknown"
    string BoardManufacturer,
    string BoardProduct)
{
    /// <summary>"Manufacturer Product" for the motherboard, trimmed.</summary>
    public string BoardDisplay => $"{BoardManufacturer} {BoardProduct}".Trim();

    /// <summary>True when no meaningful BIOS data was gathered (e.g. query denied).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Version) && string.IsNullOrWhiteSpace(BoardDisplay);
}
