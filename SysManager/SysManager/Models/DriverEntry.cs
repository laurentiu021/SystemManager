// SysManager · DriverEntry — model for installed drivers
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Models;

/// <summary>
/// Represents an installed Windows driver from Win32_PnPSignedDriver.
/// </summary>
public sealed record DriverEntry
{
    public string DeviceName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }

    /// <summary>
    /// Whether Windows reports this driver as digitally signed. <c>null</c> when the value was absent
    /// from the query result, which is not the same as unsigned.
    /// </summary>
    /// <remarks>
    /// <c>Win32_PnPSignedDriver</c> is named for exactly this, and the query selected four other
    /// properties and dropped it (#1581) — so the tab that could answer "is this from who it claims?"
    /// showed only <c>Manufacturer</c>, which is a string the driver package supplies about itself.
    /// <para>Nullable on purpose. A missing value must not render as "Unsigned": that would turn "Windows
    /// did not tell us" into an accusation, and on this tab an accusation is what sends someone hunting
    /// for a driver to remove. Absent stays blank.</para>
    /// </remarks>
    public bool? IsSigned { get; init; }

    /// <summary>Formatted date for display (yyyy-MM-dd or empty).</summary>
    public string DriverDateDisplay => DriverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// What the signature column shows: <c>Signed</c>, <c>Unsigned</c>, or empty when unknown.
    /// </summary>
    /// <remarks>
    /// Deliberately says "Signed", not "Safe". A signature proves who published the driver, not that the
    /// driver is good — and Windows itself will load a signed driver from anyone with a valid certificate.
    /// Overstating it here would be worse than the unverified <c>Manufacturer</c> string it sits beside.
    /// </remarks>
    public string SignatureDisplay => IsSigned switch
    {
        true => "Signed",
        false => "Unsigned",
        null => "",
    };
}
