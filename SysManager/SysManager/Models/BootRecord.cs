// SysManager · BootRecord
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A single boot's performance summary, parsed from a Diagnostics-Performance event 100.
/// Times are in milliseconds as Windows reports them. <see cref="BootTimeMs"/> is the total
/// time to a usable desktop.
/// </summary>
public sealed record BootRecord(
    DateTime BootTime,
    long BootTimeMs,
    long MainPathBootTimeMs,
    long PostBootTimeMs)
{
    /// <summary>Total boot time as whole seconds, one decimal.</summary>
    public string BootSecondsDisplay => $"{BootTimeMs / 1000.0:F1} s";

    public string MainPathDisplay => $"{MainPathBootTimeMs / 1000.0:F1} s";
    public string PostBootDisplay => $"{PostBootTimeMs / 1000.0:F1} s";
    public string WhenDisplay => BootTime.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// A component (app, driver, service, or device) that Windows flagged as slowing boot,
/// parsed from a Diagnostics-Performance degradation event (101–110). <see cref="DurationMs"/>
/// is the delay attributed to it.
/// </summary>
public sealed record BootDegradation(
    DateTime When,
    string Kind,       // "Application" / "Driver" / "Service" / "Device" / "Background"
    string Name,
    long DurationMs)
{
    public string DurationDisplay => DurationMs >= 1000 ? $"{DurationMs / 1000.0:F1} s" : $"{DurationMs} ms";
    public string WhenDisplay => When.ToString("yyyy-MM-dd HH:mm");
}
