// SysManager · WindowFit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.Helpers;

/// <summary>
/// Where a window too big for its screen goes instead, before it is first shown (#2704).
/// </summary>
/// <remarks>
/// The window opened 1280 by 820 on every screen, and nothing read the screen's size. A 1366 x 768 screen at 100% has
/// a work area about 728 tall, and a 1920 x 1080 one at 150% about 688, so the bottom of the window, which holds every
/// tab's status line and its bottom row of buttons, was under the taskbar or off the screen. Given the window's bounds
/// as it would open, its minimum size and its screen's work area, all in the same units, this says where it goes: where
/// it is when it fits, otherwise cut to the work area and centred in it. A screen smaller than the minimum size
/// lowers the minimum to the work area and opens the window maximized, with the work area to go back to.
/// </remarks>
internal static class WindowFit
{
    /// <summary>What to do with the window before it is shown.</summary>
    /// <param name="Bounds">Where to put it, or null to leave it where it is.</param>
    /// <param name="Minimum">The minimum size to give it, the one it had unless the screen is smaller.</param>
    /// <param name="Maximize">Whether to open it maximized: the screen is smaller than its minimum size.</param>
    internal readonly record struct Placement(Rect? Bounds, Size Minimum, bool Maximize);

    internal static Placement Place(Rect window, Size minimum, Rect workArea)
    {
        if (workArea.Contains(window)) return new(null, minimum, false);

        var fitted = new Size(Math.Min(minimum.Width, workArea.Width), Math.Min(minimum.Height, workArea.Height));
        var width = Math.Min(window.Width, workArea.Width);
        var height = Math.Min(window.Height, workArea.Height);
        var bounds = new Rect(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
        return new(bounds, fitted, fitted != minimum);
    }
}
