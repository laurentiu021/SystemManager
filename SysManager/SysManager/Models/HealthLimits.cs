// SysManager · HealthLimits
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The limits past which SysManager advises a restart or closing apps, defined once for every place that gives that
/// advice.
/// </summary>
/// <remarks>
/// The Dashboard's Quick Tune-Up card warned at 14 days of uptime and 85% of memory in use, while the Health Score card
/// in the row below it recommended a restart past 7 days and closing apps past 80%, so in between the two cards said
/// opposite things (#2877). Declared in Models because <see cref="TuneUpResult"/> needs them and a model cannot depend
/// on a service.
/// <para>The notification-area alerts interrupt rather than wait to be read, and repeat every four hours while the
/// condition lasts, so they keep limits of their own, further out, declared here beside the advice they follow.</para>
/// </remarks>
internal static class HealthLimits
{
    /// <summary>Days of uptime past which a restart is advised.</summary>
    public const double RestartAdvisedAfterDays = 7;

    /// <summary>Memory in use, in percent, past which closing apps is advised.</summary>
    public const double CloseAppsAdvisedAbovePercent = 80;

    /// <summary>Days of uptime past which the notification area suggests a restart.</summary>
    public const double RestartNotifiedAfterDays = 14;

    /// <summary>Memory in use, in percent, past which the notification area says memory is high.</summary>
    public const double HighMemoryNotifiedAbovePercent = 90;

    /// <summary>Whether this much uptime makes a restart advisable.</summary>
    public static bool AdvisesRestart(TimeSpan uptime) => uptime.TotalDays > RestartAdvisedAfterDays;

    /// <summary>Whether this much memory in use makes closing apps advisable.</summary>
    public static bool AdvisesClosingApps(double usedPercent) => usedPercent > CloseAppsAdvisedAbovePercent;
}
