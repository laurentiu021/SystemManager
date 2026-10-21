// SysManager · WheelPassThrough
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Hands the mouse wheel on to the page when a list has nothing left to scroll that way (#2817).
/// </summary>
/// <remarks>
/// A DataGrid's template holds a plain <see cref="ScrollViewer"/>, which takes every turn of the wheel whether or not
/// it can move, so the page around a table stopped scrolling while the pointer was over it: on System Health's two
/// tables, which are as tall as their rows and never scroll, and on Speed Test's and Drivers' tables once they reached
/// either end. Set on a list, this sees the turn before the list's own viewer does, and when that viewer cannot move
/// the way the wheel turned, it marks the turn handled and raises it again on the list's parent, so the page's viewer
/// scrolls instead:
/// <code>helpers:WheelPassThrough.ToPage="True"</code>
/// Every DataGrid has it from the app's DataGrid style. A list that can still move the wheel's way keeps the turn.
/// </remarks>
public static class WheelPassThrough
{
    /// <summary>Turns handing the wheel on to the page on or off for the element it is set on.</summary>
    public static readonly DependencyProperty ToPageProperty = DependencyProperty.RegisterAttached(
        "ToPage", typeof(bool), typeof(WheelPassThrough), new PropertyMetadata(false, OnToPageChanged));

    // Half a pixel, or half a row for a list that scrolls by rows: less than any turn of the wheel moves it.
    private const double Slack = 0.5;

    public static bool GetToPage(UIElement element) => (bool)element.GetValue(ToPageProperty);
    public static void SetToPage(UIElement element, bool value) => element.SetValue(ToPageProperty, value);

    private static void OnToPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement list) return;
        if (OwnViewer(list) is { } viewer && CanMove(viewer, e.Delta)) return;
        if (ParentOf(list) is not { } parent) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = list,
        });
    }

    /// <summary>
    /// Whether <paramref name="viewer"/> can move the way a turn of <paramref name="delta"/> scrolls: up for a positive
    /// one, down for a negative one.
    /// </summary>
    internal static bool CanMove(ScrollViewer viewer, int delta) =>
        delta > 0
            ? viewer.VerticalOffset > Slack
            : viewer.ScrollableHeight - viewer.VerticalOffset > Slack;

    /// <summary>The list's own scroll viewer: the nearest one inside it.</summary>
    private static ScrollViewer? OwnViewer(DependencyObject list)
    {
        var queue = new Queue<DependencyObject>([list]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer viewer) return viewer;
                queue.Enqueue(child);
            }
        }

        return null;
    }

    /// <summary>The nearest element above the list that can take the turn.</summary>
    private static UIElement? ParentOf(DependencyObject list)
    {
        for (var d = VisualTreeHelper.GetParent(list); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement element) return element;

        return null;
    }
}
