// SysManager · BatteryInfo — model for battery health data
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
    [NotifyPropertyChangedFor(nameof(DesignCapacityDisplay))]
    private uint _designCapacityMWh;       // milliwatt-hours

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(WearPercent))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(WearDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCapacityData))]
    [NotifyPropertyChangedFor(nameof(FullChargeCapacityDisplay))]
    private uint _fullChargeCapacityMWh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CycleCountDisplay))]
    private int _cycleCount;

    /// <summary>
    /// True when Windows answered with a cycle count. 0 is a real count for a new battery, so it cannot also mean
    /// "not read", which is what it meant when the read needed administrator rights and was refused (#2623).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CycleCountDisplay))]
    private bool _cycleCountRead;

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

    /// <summary>The design capacity as the details card shows it, or "Not available" when Windows gave none.</summary>
    /// <remarks>
    /// A refused read leaves it at 0, and the card read "0 mWh", as if the battery were dead rather than not read
    /// (#2623). 0 reads "Not available" for both capacities, as it already does for <see cref="HealthDisplay"/> and
    /// <see cref="WearDisplay"/>, which are worked out from them.
    /// </remarks>
    public string DesignCapacityDisplay => DesignCapacityMWh > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{DesignCapacityMWh} mWh")
        : "Not available";

    /// <summary>The full charge capacity as the details card shows it, or "Not available" when Windows gave none.</summary>
    public string FullChargeCapacityDisplay => FullChargeCapacityMWh > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{FullChargeCapacityMWh} mWh")
        : "Not available";

    /// <summary>The cycle count as the details card shows it, or "Not available" when Windows did not answer.</summary>
    public string CycleCountDisplay => CycleCountRead
        ? CycleCount.ToString(CultureInfo.InvariantCulture)
        : "Not available";
}
