// SysManager · DiskHealthReport
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Friendly, per-disk health report derived from Windows Storage reliability
/// counters (SMART-sourced). All fields nullable because not every driver
/// exposes every counter.
/// </summary>
public partial class DiskHealthReport : ObservableObject
{
    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private string _mediaType = "";       // HDD / SSD / NVMe
    [ObservableProperty] private string _busType = "";
    [ObservableProperty] private double _sizeGB;
    [ObservableProperty] private string _healthStatus = "";    // Healthy / Warning / Unhealthy
    [ObservableProperty] private double? _temperatureC;
    [ObservableProperty] private double? _temperatureMaxC;
    [ObservableProperty] private int? _wearPercent;            // 0 = new, 100 = worn out (SSD only)
    [ObservableProperty] private long? _powerOnHours;
    [ObservableProperty] private long? _readErrors;
    [ObservableProperty] private long? _writeErrors;
    [ObservableProperty] private long? _startStopCount;
    [ObservableProperty] private string _verdict = "";         // plain-English summary
    [ObservableProperty] private string _verdictColorHex = "#9AA0A6";

    /// <summary>
    /// Overall health score 0–100 (100 = perfect). Computed from wear, temperature,
    /// and error counts. Returns null when no SMART data is available.
    /// </summary>
    public int? HealthPercent
    {
        get
        {
            int score = 100;

            if (WearPercent.HasValue)
                score -= Math.Clamp(WearPercent.Value, 0, 100);

            if (TemperatureC.HasValue)
            {
                if (TemperatureC.Value > 70) score -= 30;
                else if (TemperatureC.Value > 60) score -= 15;
                else if (TemperatureC.Value > 50) score -= 5;
            }

            if (ReadErrors is > 0) score -= Math.Min((int)ReadErrors.Value * 5, 20);
            if (WriteErrors is > 0) score -= Math.Min((int)WriteErrors.Value * 5, 20);

            if (!WearPercent.HasValue && !TemperatureC.HasValue && ReadErrors is null && WriteErrors is null)
            {
                return HealthStatus switch
                {
                    "Healthy" => 100,
                    "Warning" => 60,
                    "Unhealthy" => 20,
                    _ => null
                };
            }

            return Math.Clamp(score, 0, 100);
        }
    }

    /// <summary>Color hex for the health percentage gauge.</summary>
    public string HealthPercentColorHex => HealthPercent switch
    {
        >= 80 => "#22C55E",
        >= 50 => "#F59E0B",
        >= 20 => "#F87171",
        _ => "#EF4444"
    };

    /// <summary>Color hex for the temperature reading.</summary>
    public string TemperatureColorHex => TemperatureC switch
    {
        <= 40 => "#22C55E",
        <= 50 => "#F59E0B",
        <= 60 => "#F87171",
        _ => "#EF4444"
    };

    /// <summary>Temperature as a 0–100 gauge value (0 °C = 0, 80 °C = 100).</summary>
    public int TemperatureGauge => TemperatureC.HasValue
        ? Math.Clamp((int)(TemperatureC.Value / 80.0 * 100), 0, 100)
        : 0;

    /// <summary>Wear as a 0–100 gauge value (inverted: 0 wear = 100% life remaining).</summary>
    public int WearGauge => WearPercent.HasValue
        ? Math.Clamp(100 - WearPercent.Value, 0, 100)
        : 100;

    /// <summary>Color hex for the wear gauge.</summary>
    public string WearColorHex => WearPercent switch
    {
        null => "#9AA0A6",
        <= 20 => "#22C55E",
        <= 50 => "#F59E0B",
        <= 80 => "#F87171",
        _ => "#EF4444"
    };

    /// <summary>Friendly power-on time display.</summary>
    public string PowerOnDisplay => PowerOnHours switch
    {
        null => "—",
        < 24 => $"{PowerOnHours}h",
        < 8760 => $"{PowerOnHours / 24}d {PowerOnHours % 24}h",
        _ => $"{PowerOnHours.Value / 8760.0:F1}y"
    };
}
