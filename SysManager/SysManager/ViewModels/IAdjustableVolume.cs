// SysManager · IAdjustableVolume
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.ViewModels;

/// <summary>
/// A volume the user can be in the middle of adjusting: an app's row, or the This PC card (#1588). The view sets
/// <see cref="IsUserAdjusting"/> while the slider is dragged or holds keyboard focus, so the once-a-second refresh
/// does not move the value under the user's hand. One contract for both, so the view's slider handlers serve
/// both.
/// </summary>
public interface IAdjustableVolume
{
    /// <summary>True while the user is dragging the slider or adjusting it from the keyboard.</summary>
    bool IsUserAdjusting { get; set; }
}
