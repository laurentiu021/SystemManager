// SysManager · LegacyPanelService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Opens classic Windows applets (Control Panel, Sound, Power Options, Device Manager,
/// etc.) via their well-known <c>control</c>/<c>*.cpl</c>/<c>*.msc</c> commands.
///
/// SECURITY: the catalog is hard-coded — no user input ever reaches <c>Process.Start</c>.
/// <see cref="Launch"/> only accepts a <see cref="LegacyPanel"/> from <see cref="Panels"/>,
/// and re-validates membership before launching. These are pure launchers; none modify the
/// system, so no elevation or confirmation is needed (the applets themselves prompt for UAC
/// when an action inside them requires it).
/// </summary>
public sealed class LegacyPanelService
{
    /// <summary>The fixed catalog of classic applets, grouped logically by order.</summary>
    public static IReadOnlyList<LegacyPanel> Panels { get; } =
    [
        new("Control Panel", "The classic Control Panel home.", "", "control.exe", ""),
        new("Sound", "Playback, recording, and sound device settings.", "", "control.exe", "mmsys.cpl"),
        new("Power Options", "Power plans and sleep/battery behavior.", "", "control.exe", "powercfg.cpl"),
        new("Network Connections", "Adapter list (ncpa.cpl) — enable, disable, properties.", "", "control.exe", "ncpa.cpl"),
        new("Region", "Date, time, and regional formats.", "", "control.exe", "intl.cpl"),
        new("System Properties", "Advanced system settings, performance, environment vars.", "", "SystemPropertiesAdvanced.exe", ""),
        new("User Accounts", "Classic user account management (netplwiz).", "", "netplwiz.exe", ""),
        new("Device Manager", "Hardware devices and driver management.", "", "mmc.exe", "devmgmt.msc"),
        new("Computer Management", "Disks, services, event viewer, and more in one console.", "", "mmc.exe", "compmgmt.msc"),
        new("Programs and Features", "Classic installed-programs uninstaller (appwiz.cpl).", "", "control.exe", "appwiz.cpl"),
        new("Mouse", "Pointer, buttons, and wheel settings.", "", "control.exe", "main.cpl"),
        new("Date and Time", "System clock and time-zone settings.", "", "control.exe", "timedate.cpl"),
    ];

    /// <summary>
    /// Launches the given applet. Returns false if the panel is not from the known catalog
    /// or the process could not start (logged). Never throws on a launch failure.
    /// </summary>
    public bool Launch(LegacyPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        // Defense in depth: only ever launch a catalog entry, never arbitrary input.
        if (!Panels.Contains(panel))
        {
            Log.Warning("LegacyPanel: refusing to launch unknown panel {Name}", panel.Name);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = panel.FileName,
                Arguments = panel.Arguments,
                UseShellExecute = true
            };
            Process.Start(psi)?.Dispose();
            Log.Information("LegacyPanel: opened {Name}", panel.Name);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("LegacyPanel: could not open {Name}: {Error}", panel.Name, ex.Message);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("LegacyPanel: could not open {Name}: {Error}", panel.Name, ex.Message);
            return false;
        }
    }
}
