// SysManager · BatteryInfo — model for battery health data
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Battery health snapshot from WMI / Win32_Battery.
/// </summary>
public partial class BatteryInfo : ObservableObject
{
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";          // Charging / Discharging / Full / AC (no battery)
    [ObservableProperty] private int _chargePercent;            // 0-100
    [ObservableProperty] private uint _designCapacityMWh;       // milliwatt-hours
    [ObservableProperty] private uint _fullChargeCapacityMWh;
    [ObservableProperty] private int _cycleCount;
    [ObservableProperty] private int _estimatedRuntimeMinutes;  // -1 = unlimited (AC)
    [ObservableProperty] private string _chemistry = "";        // LiIon, NiMH, etc.
    [ObservableProperty] private string _manufacturer = "";

    /// <summary>Health percentage: FullCharge / Design × 100.</summary>
    public double HealthPercent =>
        DesignCapacityMWh > 0
            ? Math.Round(FullChargeCapacityMWh * 100.0 / DesignCapacityMWh, 1)
            : 0;

    /// <summary>Wear level: 100 − HealthPercent.</summary>
    public double WearPercent =>
        DesignCapacityMWh > 0
            ? Math.Round(100.0 - HealthPercent, 1)
            : 0;

    /// <summary>Formatted estimated runtime.</summary>
    public string RuntimeDisplay => EstimatedRuntimeMinutes switch
    {
        -1 => "Plugged in",
        0 => "Calculating…",
        _ => $"{EstimatedRuntimeMinutes / 60}h {EstimatedRuntimeMinutes % 60}m"
    };
}
