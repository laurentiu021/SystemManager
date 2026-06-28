// SysManager · IWindowsThemeService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="WindowsThemeService"/> — switches the per-user Windows
/// light/dark theme and loads/saves the dark-mode schedule. Extracting this interface
/// lets <c>DarkModeViewModel</c>'s mutating command paths (SwitchToDark / SwitchToLight
/// and schedule persistence) be unit-tested with a substituted service instead of
/// writing HKCU and flipping the real Windows theme (Gate-ARCH: system-mutating
/// services are testable).
///
/// <para>Only the instance members are abstracted; the pure
/// <see cref="WindowsThemeService.ShouldBeDark"/> schedule predicate remains static on
/// the concrete class.</para>
/// </summary>
public interface IWindowsThemeService
{
    /// <summary>Read the current Windows app theme (absent value = Light, the OS default).</summary>
    WindowsTheme GetCurrentTheme();

    /// <summary>
    /// Set the Windows theme. When <paramref name="includeSystem"/>, also flips the
    /// taskbar/Start. Returns false if the registry write was denied.
    /// </summary>
    bool SetTheme(bool dark, bool includeSystem);

    /// <summary>Load the saved schedule, or defaults if none exists / it's unreadable.</summary>
    DarkModeSchedule LoadSchedule();

    /// <summary>Persist the schedule as indented JSON in the app's roaming AppData folder.</summary>
    void SaveSchedule(DarkModeSchedule schedule);
}
