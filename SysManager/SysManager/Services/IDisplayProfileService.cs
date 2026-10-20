// SysManager · IDisplayProfileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="DisplayProfileService"/>: lists the displays and the modes each supports, sets a mode for
/// the session, and says when Windows reports a display change. Extracting this interface lets
/// <c>DisplayProfileViewModel</c> be tested over a substitute instead of the displays of the PC running the tests.
/// </summary>
public interface IDisplayProfileService
{
    /// <summary>
    /// Raised when Windows reports a display change: a screen connected or removed, or a mode set, SysManager's own
    /// included. Raised on whichever thread the notification comes in on.
    /// </summary>
    event EventHandler? DisplaysChanged;

    /// <summary>Enumerate active display adapters.</summary>
    IReadOnlyList<DisplayDevice> GetDisplays();

    /// <summary>The mode currently in effect for a device, or null if it can't be read.</summary>
    /// <param name="deviceName">The adapter's device name, as <see cref="GetDisplays"/> lists it.</param>
    DisplayMode? GetCurrentMode(string deviceName);

    /// <summary>All distinct supported modes (by width×height×refresh), newest-first by resolution then refresh.</summary>
    /// <param name="deviceName">The adapter's device name, as <see cref="GetDisplays"/> lists it.</param>
    IReadOnlyList<DisplayMode> GetSupportedModes(string deviceName);

    /// <summary>
    /// Apply a mode for the current session (reverts on logoff/reboot). Validates with CDS_TEST first. Returns true on
    /// success; otherwise sets <paramref name="error"/>.
    /// </summary>
    /// <param name="deviceName">The adapter's device name, as <see cref="GetDisplays"/> lists it.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="refreshHz">The refresh rate in hertz.</param>
    /// <param name="error">Why nothing was changed, in words for the status line.</param>
    bool TryApplyMode(string deviceName, int width, int height, int refreshHz, out string error);
}
