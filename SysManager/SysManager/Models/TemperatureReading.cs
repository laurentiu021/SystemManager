// SysManager · TemperatureReading — represents a single temperature sensor value
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Models;

public sealed record TemperatureReading(
    string Component,
    string SensorName,
    double? TemperatureC,
    bool RequiresAdmin = false)
{
    public string DisplayValue => TemperatureC.HasValue
        ? $"{TemperatureC.Value:F0}°C"
        : RequiresAdmin ? "Requires admin" : "N/A";

    public string ColorHex => TemperatureC switch
    {
        null => "#6B7B8F",
        <= 45 => StatusColors.Good,
        <= 65 => StatusColors.Info,
        <= 80 => StatusColors.Warning,
        _ => StatusColors.Bad
    };
}
