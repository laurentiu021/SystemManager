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
    // snapshot). A slider is "being adjusted" while EITHER a mouse drag is in progress OR it
    // holds keyboard focus (arrow-key / track-click fine-tuning). These two sources are tracked
    // independently and OR'd, so ending a mouse drag does NOT clear the guard while the slider
    // still has keyboard focus (the drag-then-arrow-key workflow). View-only concern (routed
    // input events), so it lives in code-behind.
    // A slider is "being adjusted" while a mouse drag is in progress OR it holds keyboard focus.
    // Extracted as a pure static so the OR-of-two-sources logic (the fix for the drag-then-arrow
    // clobber) is unit-testable without a rendered window.
    internal static bool ComputeAdjusting(bool dragging, bool keyboardFocused) => dragging || keyboardFocused;

    // Applies the combined guard to a row from the two independent sources. Extracted (internal)
    // so a test can drive the exact event orderings the four handlers produce — in particular the
    // Audit-3 regression: drag ends (dragging=false) while the slider still holds keyboard focus,
    // which must leave the row STILL adjusting. This is the wiring that actually held the bug, so
    // it is what needs coverage — not just the terminal OR.
    internal static void ApplyAdjustingState(AudioSessionRowViewModel? row, bool dragging, bool keyboardFocused)
    {
        if (row is not null) row.IsUserAdjusting = ComputeAdjusting(dragging, keyboardFocused);
    }

    private static void Recompute(Slider slider) =>
        ApplyAdjustingState(slider.DataContext as AudioSessionRowViewModel,
            slider.Tag is true, slider.IsKeyboardFocusWithin);

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Slider s) { s.Tag = true; Recompute(s); }
    }

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider s) { s.Tag = false; Recompute(s); }
    }

    private void VolumeSlider_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Slider s) Recompute(s);
    }

    private void VolumeSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Slider s) Recompute(s);
    }
}
