// SysManager · BatteryCapacityPoint
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One entry of the capacity history Windows keeps for a battery: how much it held when full, against what it
/// held when new, for the period starting at <paramref name="Date"/>.
/// </summary>
/// <param name="Date">The local start of the period the entry covers: a week for older periods, a day for recent ones.</param>
/// <param name="DesignCapacityMWh">What the battery was built to hold.</param>
/// <param name="FullChargeCapacityMWh">What it held when fully charged in that period.</param>
public sealed record BatteryCapacityPoint(DateTime Date, long DesignCapacityMWh, long FullChargeCapacityMWh)
{
    /// <summary>
    /// Full-charge capacity as a percentage of design capacity: the same reading the tab's Health figure gives,
    /// so the chart's axis means the same thing as the number above it. A new battery can read slightly over 100.
    /// </summary>
    public double HealthPercent => DesignCapacityMWh > 0 ? FullChargeCapacityMWh * 100.0 / DesignCapacityMWh : 0;
}
