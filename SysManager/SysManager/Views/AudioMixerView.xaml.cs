// SysManager · AudioMixerView — per-app volume mixer UI
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SysManager.ViewModels;

namespace SysManager.Views;

public partial class AudioMixerView : UserControl
{
    public AudioMixerView() => InitializeComponent();

    // While the user drags a row's volume slider, flag its row so a background refresh tick
    // does not overwrite the value mid-drag (the thumb would jump to a stale snapshot value).
    // These are view-only concerns (routed Thumb drag events), so they live in code-behind.
    private static void SetAdjusting(object sender, bool adjusting)
    {
        if (sender is Slider { DataContext: AudioSessionRowViewModel row })
            row.IsUserAdjusting = adjusting;
    }

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e) => SetAdjusting(sender, true);

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e) => SetAdjusting(sender, false);
}
