// SysManager · BatteryInfo — model for battery health data
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Battery health snapshot from WMI / Win32_Battery.
/// </summary>
public sealed partial class BatteryInfo : ObservableObject
{
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";          // Charging / Discharging / Full / AC (no battery)
    [ObservableProperty] private int _chargePercent;            // 0-100
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(WearPercent))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(WearDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCapacityData))]
    private uint _designCapacityMWh;       // milliwatt-hours

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(WearPercent))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(WearDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCapacityData))]
    private uint _fullChargeCapacityMWh;
    [ObservableProperty] private int _cycleCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeDisplay))]
    private int _estimatedRuntimeMinutes;  // -1 = unlimited (AC)
    [ObservableProperty] private string _chemistry = "";        // LiIon, NiMH, etc.
    [ObservableProperty] private string _manufacturer = "";

    /// <summary>
    /// Health percentage: FullCharge / Design × 100.
    /// Returns -1 when capacity data is unavailable (e.g. no admin elevation
    /// for root\WMI queries) to avoid false-critical health scores.
    /// </summary>
    public double HealthPercent =>
        DesignCapacityMWh > 0 && FullChargeCapacityMWh > 0
            ? Math.Min(Math.Round(FullChargeCapacityMWh * 100.0 / DesignCapacityMWh, 1), 100)
            : -1;

    /// <summary>Wear level: 100 − HealthPercent. Returns -1 when data unavailable.</summary>
    public double WearPercent =>
        HealthPercent >= 0
            ? Math.Max(Math.Round(100.0 - HealthPercent, 1), 0)
            : -1;

    /// <summary>Formatted estimated runtime.</summary>
    public string RuntimeDisplay => EstimatedRuntimeMinutes switch
    {
        -1 => "Plugged in",
        0 => "Calculating…",
        _ => $"{EstimatedRuntimeMinutes / 60}h {EstimatedRuntimeMinutes % 60}m"
    };

    /// <summary>True when capacity data could be read, so health and wear are meaningful.</summary>
    public bool HasCapacityData => HealthPercent >= 0;

    /// <summary>
    /// Formatted health. The -1 sentinel means "capacity could not be read", usually because
    /// the root\WMI query needs elevation — so it must not reach the screen as a number.
    /// Bound instead of HealthPercent, which rendered literally as "-1%" next to a "%" suffix
    /// in the view and read as a nonsensical measurement rather than as missing data.
    /// </summary>
    public string HealthDisplay => HasCapacityData ? $"{HealthPercent}%" : "Not available";

    /// <summary>Formatted wear level. Same sentinel handling as <see cref="HealthDisplay"/>.</summary>
    public string WearDisplay => HasCapacityData ? $"{WearPercent}%" : "Not available";
}
