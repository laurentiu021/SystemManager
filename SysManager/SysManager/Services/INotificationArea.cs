// SysManager · INotificationArea
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// The notification area's side of telling the user something while SysManager's window is not on screen.
/// </summary>
/// <remarks>
/// A toast is drawn inside the window and hides itself after five seconds, so with the window minimized or hidden in the
/// notification area, which is how New App Alerts is usually left monitoring, nobody saw it (#2769). Implemented by
/// <see cref="TrayIconService"/>, which knows the window and owns the tray icon.
/// </remarks>
public interface INotificationArea
{
    /// <summary>
    /// Shows a Windows notification from the tray icon when the window is not on screen and notifications are on, and
    /// says whether it did, so the caller shows its toast in the window when it did not.
    /// </summary>
    bool TryNotifyWhileAway(string title, string message);
}
