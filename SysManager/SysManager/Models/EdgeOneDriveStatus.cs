// SysManager · EdgeOneDriveStatus
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A read-only snapshot of the Microsoft Edge and OneDrive integration state that the
/// Edge/OneDrive Remover tab surfaces. All fields are raw, independently-observed signals;
/// the ViewModel derives the user-facing "de-integrated / active" labels from them so the
/// service stays free of presentation logic.
/// <para>
/// OneDrive is handled per-user (no elevation): it can be uninstalled with
/// <c>OneDriveSetup.exe /uninstall</c> and unpinned from the Explorer navigation pane, both
/// reversible. Edge is NEVER uninstalled — only a fully reversible "disable &amp;
/// de-integrate" is offered (turn off background mode / startup boost via Group Policy and
/// disable its auto-update scheduled tasks), which needs administrator rights.
/// </para>
/// </summary>
public sealed record EdgeOneDriveStatus(
    bool OneDriveInstalled,
    bool OneDriveRunning,
    bool OneDrivePinned,
    bool EdgeInstalled,
    bool EdgeUpdateTasksEnabled,
    bool EdgeBackgroundDisabled)
{
    /// <summary>The all-false snapshot returned when the state could not be read.</summary>
    public static EdgeOneDriveStatus Empty { get; } = new(false, false, false, false, false, false);
}
