// SysManager · BootRecord
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

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
    public string BootSecondsDisplay => string.Create(CultureInfo.InvariantCulture, $"{BootTimeMs / 1000.0:F1} s");

    public string MainPathDisplay => string.Create(CultureInfo.InvariantCulture, $"{MainPathBootTimeMs / 1000.0:F1} s");
    public string PostBootDisplay => string.Create(CultureInfo.InvariantCulture, $"{PostBootTimeMs / 1000.0:F1} s");
    public string WhenDisplay => BootTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
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
    public string DurationDisplay => DurationMs >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{DurationMs / 1000.0:F1} s") : $"{DurationMs} ms";
    public string WhenDisplay => When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The tab that can switch this component off, derived from <see cref="Kind"/>.
    /// </summary>
    /// <remarks>
    /// Boot Analyzer knew exactly which component cost the user 4.2 seconds of boot and offered no way to
    /// act on it — the word "Startup" did not appear in its view at all (#1504). "My laptop takes forever
    /// to start" is this persona's commonest complaint, and this is the tab that answers it, then
    /// abandoned them at the answer.
    /// <para>A Service goes to Services; an Application or a Background task goes to Startup Manager.
    /// Drivers and Devices get nothing on purpose: this app has no tab that disables a driver, and sending
    /// someone to Startup Manager to look for one they will not find is worse than an honest dead end.</para>
    /// </remarks>
    public string NavTargetId => Kind switch
    {
        "Service" => "nav-services",
        "Application" or "Background" => "nav-startup",
        _ => "",
    };

    /// <summary>True when there is a tab that can act on this component.</summary>
    public bool CanNavigate => NavTargetId.Length > 0;

    /// <summary>
    /// What the link says, naming the destination rather than the component.
    /// </summary>
    /// <remarks>
    /// "Open Services" rather than "Fix Spooler": the row already names the component, and a label that
    /// repeats it says less about where the click goes — which is the one thing the user cannot see.
    /// </remarks>
    public string NavLabel => Kind switch
    {
        "Service" => "Open Services",
        "Application" or "Background" => "Manage startup items",
        _ => "",
    };
}
