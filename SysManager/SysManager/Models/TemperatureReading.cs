// SysManager · TemperatureReading — represents a single temperature sensor value
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Models;

/// <param name="NameIsPlaceholder">
/// True when the producer could not get a real name for this sensor and invented one. Only a reading so
/// flagged may have its name replaced later: the storage enricher used to overwrite every storage name by
/// list position, so a name LibreHardwareMonitor reported correctly was clobbered with whatever name happened
/// to sit at the same ordinal in an independently-ordered WMI list.
/// </param>
public sealed record TemperatureReading(
    string Component,
    string SensorName,
    double? TemperatureC,
    bool RequiresAdmin = false,
    bool NameIsPlaceholder = false)
{
    public string DisplayValue => TemperatureC.HasValue
        ? $"{TemperatureC.Value:F0}°C"
        : RequiresAdmin ? "Requires admin" : "N/A";

    public string ColorHex => TemperatureC switch
    {
        // No reading (no sensor, or admin required) — the muted tone the app uses everywhere for
        // "nothing to report", rather than a literal that cannot follow the theme.
        null => StatusColors.Neutral,
        <= 45 => StatusColors.Good,
        <= 65 => StatusColors.Info,
        <= 80 => StatusColors.Warning,
        _ => StatusColors.Bad
    };
}
