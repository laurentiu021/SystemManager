// SysManager · WindowsUpdatePolicy
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Models;

/// <summary>
/// A snapshot of the Windows Update deferral policy currently in effect, read from the
/// documented WindowsUpdate policy registry keys. All fields are reversible — clearing
/// them returns Windows to its out-of-box update behavior.
/// </summary>
public sealed record WindowsUpdatePolicy(
    bool DeferFeatureUpdates,
    int FeatureDeferDays,
    bool PauseActive,
    DateTime? PauseUntil)
{
    /// <summary>Plain-English summary of the active policy for the status line.</summary>
    public string Summary =>
        PauseActive && PauseUntil is { } until
            ? $"Updates paused until {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}."
            : DeferFeatureUpdates
                ? $"Feature updates deferred {FeatureDeferDays} day(s); security updates still install."
                : "Default — Windows manages update timing.";
}
