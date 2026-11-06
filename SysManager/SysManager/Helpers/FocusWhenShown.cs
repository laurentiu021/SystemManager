// SysManager · FocusWhenShown
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SysManager.Helpers;

/// <summary>
/// Moves keyboard focus to a control when it appears, unless focus is somewhere the user put it (#2883).
/// </summary>
/// <remarks>
/// Display Profiles shows Keep and Revert now after Apply, and the previous mode comes back 15 seconds later. Apply is
/// disabled while the panel re-trains, which takes focus off it, so someone using the keyboard or a screen reader was
/// left with focus nowhere and fifteen seconds to find Keep. Set on the control that should take focus:
/// <code>helpers:FocusWhenShown.Take="True"</code>
/// <para>As <see cref="RebuildFocus"/> does, focus is taken only from a place nobody chose: nowhere, the window, or one
/// of the control's ancestors, which is where WPF moves focus from a control that was disabled. Focus the user moved
/// to another control stays there, and a window in the background does not take focus.</para>
/// <para>The move runs at <see cref="DispatcherPriority.Input"/>, after the layout pass that shows the control, which
/// can only take focus once it is laid out.</para>
/// </remarks>
public static class FocusWhenShown
{
    /// <summary>Turns taking focus on or off for the control it is set on.</summary>
    public static readonly DependencyProperty TakeProperty = DependencyProperty.RegisterAttached(
        "Take", typeof(bool), typeof(FocusWhenShown), new PropertyMetadata(false, OnTakeChanged));

    public static bool GetTake(UIElement element) => (bool)element.GetValue(TakeProperty);
    public static void SetTake(UIElement element, bool value) => element.SetValue(TakeProperty, value);

    private static void OnTakeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.IsVisibleChanged -= OnIsVisibleChanged;
        if (e.NewValue is true) element.IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not UIElement element) return;

        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!element.IsVisible || Window.GetWindow(element) is not { IsActive: true } window) return;
            if (FocusIsUnchosen(Keyboard.FocusedElement as DependencyObject, element, window)) element.Focus();
        }));
    }

    /// <summary>
    /// Whether focus at <paramref name="focused"/> is somewhere nobody chose: nowhere, <paramref name="window"/>, or
    /// one of <paramref name="target"/>'s ancestors. False when it is on the target already.
    /// </summary>
    internal static bool FocusIsUnchosen(DependencyObject? focused, DependencyObject target, DependencyObject window)
    {
        if (focused is null || ReferenceEquals(focused, window)) return true;
        return !ReferenceEquals(focused, target) && RebuildFocus.IsWithin(target, focused);
    }
}
