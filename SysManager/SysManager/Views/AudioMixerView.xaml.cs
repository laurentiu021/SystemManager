// SysManager · AudioMixerView — per-app volume mixer UI
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SysManager.ViewModels;

namespace SysManager.Views;

public partial class AudioMixerView : UserControl
{
    public AudioMixerView() => InitializeComponent();

    // While the user is adjusting a row's volume slider, flag its row so a background refresh
    // tick does not overwrite the value mid-adjust (the thumb/value would jump to a stale
    // snapshot). Covers BOTH mouse-drag (Thumb.DragStarted/Completed) and keyboard/track-click
    // (the slider holding keyboard focus). These are view-only concerns (routed input events),
    // so they live in code-behind.
    private static void SetAdjusting(object sender, bool adjusting)
    {
        if (sender is Slider { DataContext: AudioSessionRowViewModel row })
            row.IsUserAdjusting = adjusting;
    }

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e) => SetAdjusting(sender, true);

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e) => SetAdjusting(sender, false);

    private void VolumeSlider_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SetAdjusting(sender, true);

    private void VolumeSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SetAdjusting(sender, false);
}
