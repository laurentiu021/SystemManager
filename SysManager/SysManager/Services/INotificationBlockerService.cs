// SysManager · INotificationBlockerService — testable seam for the Notification Blocker
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="NotificationBlockerService"/> so the ViewModel can be unit-tested with a
/// substituted implementation (no real registry). Mirrors the established interface-seam pattern
/// (<see cref="IAppBlockerService"/>, <see cref="ISettingsWatchdogService"/>).
/// </summary>
public interface INotificationBlockerService
{
    /// <summary>Whether Windows toast notifications are enabled machine-wide for this user.</summary>
    bool IsGlobalToastEnabled();

    /// <summary>Turns all toast notifications on/off for this user. Returns true on success.</summary>
    bool SetGlobalToastEnabled(bool enabled);

    /// <summary>Every app Windows has recorded as a notification sender, most-active first.</summary>
    IReadOnlyList<NotificationApp> GetApps();

    /// <summary>Allows or mutes one app's notifications. Returns true on success.</summary>
    bool SetAppEnabled(string aumid, bool enabled);
}
