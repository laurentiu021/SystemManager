// SysManager · TemperatureReading — represents a single temperature sensor value
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
        <= 45 => "#22C55E",
        <= 65 => "#3B82F6",
        <= 80 => "#F59E0B",
        _ => "#EF4444"
    };
}
