// SysManager · RebuildFocus
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace SysManager.Helpers;

/// <summary>
/// Puts keyboard focus back in a list after the list's rows are rebuilt (#2609).
/// </summary>
/// <remarks>
/// A list refreshed with <see cref="BulkObservableCollection{T}.ReplaceWith"/> gets a single <c>Reset</c>, and WPF throws
/// every row away and builds new ones. The focused button or cell goes with its old row, focus falls back to the window,
/// and someone using the keyboard or a screen reader has to start again from the sidebar. Set on a list, this remembers
/// where in it focus was, and after the rows change it focuses the same control in the same row if that row is still
/// there, the same control in the row now in its place if it is not, and the list, or the control after it, when no row
/// is left:
/// <code>helpers:RebuildFocus.Keep="True"</code>
/// Every DataGrid has it from the app's DataGrid style.
/// <para>Focus is moved back only from a place nobody chose: nowhere, the window, an element that left the screen with its
/// row, the list itself, or one of the list's ancestors, which is where WPF moves focus from a control that was disabled
/// for the job that rebuilt the list. Focus the user moved anywhere else stays there. The window also has to be
/// active and the keyboard the last input used, the test WPF makes before it draws a focus outline, so a list that
/// refreshes on a timer does not scroll back to a row a mouse user has scrolled away from, and a window in the
/// background does not take focus.</para>
/// <para>The restore runs at <see cref="DispatcherPriority.Loaded"/>, after the layout pass that builds the new rows and
/// before the <see cref="DispatcherPriority.Input"/> pass in which WPF decides where focus goes once the focused element
/// has left the tree.</para>
/// </remarks>
public static class RebuildFocus
{
    /// <summary>Turns keeping focus on or off for the list it is set on.</summary>
    public static readonly DependencyProperty KeepProperty = DependencyProperty.RegisterAttached(
        "Keep", typeof(bool), typeof(RebuildFocus), new PropertyMetadata(false, OnKeepChanged));

    private static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker", typeof(Tracker), typeof(RebuildFocus), new PropertyMetadata(null));

    public static bool GetKeep(ItemsControl list) => (bool)list.GetValue(KeepProperty);
    public static void SetKeep(ItemsControl list, bool value) => list.SetValue(KeepProperty, value);

    private static void OnKeepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;

        (list.GetValue(TrackerProperty) as Tracker)?.Detach();
        list.ClearValue(TrackerProperty);
        if (e.NewValue is true) list.SetValue(TrackerProperty, new Tracker(list));
    }

    /// <summary>Where in a list focus was.</summary>
    /// <param name="Item">The row's item, which finds the row again when the rebuild kept the same objects.</param>
    /// <param name="Index">The row's position in the list as shown, for when the row has gone.</param>
    /// <param name="Name">The focused control's accessible name, which on most rows also names the row's item.</param>
    /// <param name="Ordinal">Which of the row's focusable controls it was, counted in tree order.</param>
    /// <param name="Kind">Its type, so the control at that position is taken only if it is the same kind.</param>
    internal sealed record Place(object? Item, int Index, string? Name, int Ordinal, Type Kind);

    /// <summary>Where focus is when the rows have changed.</summary>
    private enum FocusAt
    {
        /// <summary>Nowhere anything put it on purpose, so it goes back into the list.</summary>
        Lost,

        /// <summary>On an element in the list that is still on screen, so nothing needs doing.</summary>
        InTheList,

        /// <summary>Somewhere the user moved it, which is where it stays.</summary>
        Elsewhere,
    }

    /// <summary>
    /// The row focus goes back to: the row holding the same item, else the one row holding a control with the same
    /// accessible name, else the row now at the same position, or the last one. -1 for an empty list.
    /// </summary>
    /// <param name="place">Where focus was.</param>
    /// <param name="items">The rows as they are now.</param>
    /// <param name="namedRow">The row holding a control named <see cref="Place.Name"/>, or -1 when none or several do.</param>
    internal static int RowFor(Place place, IList items, int namedRow)
    {
        if (items.Count == 0) return -1;
        if (place.Item is not null && items.IndexOf(place.Item) is >= 0 and var same) return same;
        if (namedRow >= 0 && namedRow < items.Count) return namedRow;
        return Math.Clamp(place.Index, 0, items.Count - 1);
    }

    /// <summary>
    /// The control in a row that focus goes back to: the one with the same accessible name, else the one at the same
    /// position if it is the same kind, else the first of the same kind, else the first. Null for a row with none.
    /// </summary>
    /// <param name="place">Where focus was.</param>
    /// <param name="controls">The row's focusable controls, in tree order, as <see cref="FocusablesIn"/> lists them.</param>
    internal static UIElement? ControlFor(Place place, IReadOnlyList<UIElement> controls)
    {
        if (!string.IsNullOrEmpty(place.Name))
        {
            var named = controls.Where(c => AutomationProperties.GetName(c) == place.Name).ToList();
            if (named.Count == 1) return named[0];
        }

        if (place.Ordinal >= 0 && place.Ordinal < controls.Count && controls[place.Ordinal].GetType() == place.Kind)
            return controls[place.Ordinal];

        return controls.FirstOrDefault(c => c.GetType() == place.Kind) ?? controls.FirstOrDefault();
    }

    /// <summary>The elements in <paramref name="root"/>, itself included, that can take keyboard focus, in tree order.</summary>
    /// <remarks>
    /// Whether an element is shown is read from <see cref="UIElement.Visibility"/> on the way down rather than from
    /// <see cref="UIElement.IsVisible"/>, which is false for anything not in a window on screen, so a test can build a
    /// row without one.
    /// </remarks>
    internal static List<UIElement> FocusablesIn(DependencyObject root)
    {
        var found = new List<UIElement>();
        Collect(root, found);
        return found;
    }

    private static void Collect(DependencyObject node, List<UIElement> found)
    {
        if (node is UIElement element)
        {
            if (element.Visibility != Visibility.Visible) return;
            if (element.Focusable && element.IsEnabled) found.Add(element);
        }

        if (node is not (Visual or Visual3D)) return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Collect(VisualTreeHelper.GetChild(node, i), found);
    }

    /// <summary>Where in <paramref name="list"/> <paramref name="focused"/> is, or null when it is not in one of its rows.</summary>
    internal static Place? PlaceOf(ItemsControl list, DependencyObject focused)
    {
        if (list.ContainerFromElement(focused) is not { } container) return null;

        var generator = list.ItemContainerGenerator;
        var index = generator.IndexFromContainer(container);
        if (index < 0) return null;

        var ordinal = focused is UIElement control ? FocusablesIn(container).IndexOf(control) : -1;
        return new Place(generator.ItemFromContainer(container), index, AutomationProperties.GetName(focused), ordinal,
            focused.GetType());
    }

    /// <summary>The control in <paramref name="list"/> that focus goes back to, or null when no row has one.</summary>
    internal static UIElement? TargetFor(ItemsControl list, Place place)
    {
        var row = RowFor(place, list.Items, NamedRow(list, place));
        if (row < 0 || ContainerAt(list, row) is not { } container) return null;
        return ControlFor(place, FocusablesIn(container));
    }

    /// <summary>The one row built on screen that holds a control named <see cref="Place.Name"/>, or -1.</summary>
    private static int NamedRow(ItemsControl list, Place place)
    {
        if (string.IsNullOrEmpty(place.Name)) return -1;

        var generator = list.ItemContainerGenerator;
        var found = -1;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not { } container) continue;
            if (!FocusablesIn(container).Any(c => AutomationProperties.GetName(c) == place.Name)) continue;

            // Two rows answer to the name, so it does not say which row focus was on.
            if (found >= 0) return -1;
            found = i;
        }

        return found;
    }

    /// <summary>The row at <paramref name="index"/>, brought into view first when the list builds only the rows on screen.</summary>
    private static DependencyObject? ContainerAt(ItemsControl list, int index)
    {
        var generator = list.ItemContainerGenerator;
        if (generator.ContainerFromIndex(index) is { } container) return container;

        switch (list)
        {
            case DataGrid grid:
                grid.ScrollIntoView(list.Items[index]);
                break;
            case ListBox box:
                box.ScrollIntoView(list.Items[index]);
                break;
            default:
                return null;
        }

        list.UpdateLayout();
        return generator.ContainerFromIndex(index);
    }

    /// <summary>Whether <paramref name="element"/> could take focus now: shown, enabled, focusable and on screen.</summary>
    private static bool CanTakeFocus(DependencyObject element) =>
        PresentationSource.FromDependencyObject(element) is not null
        && element switch
        {
            UIElement e => e.Focusable && e.IsEnabled && e.IsVisible,
            ContentElement e => e.Focusable && e.IsEnabled,
            _ => false,
        };

    /// <summary>Whether <paramref name="element"/> is <paramref name="ancestor"/> or inside it.</summary>
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = element; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>Follows focus in one list, and puts it back after the list's rows change.</summary>
    private sealed class Tracker
    {
        private readonly ItemsControl _list;
        private Place? _place;
        private DispatcherOperation? _restore;

        public Tracker(ItemsControl list)
        {
            _list = list;
            _list.GotKeyboardFocus += OnGotKeyboardFocus;
            _list.LostKeyboardFocus += OnLostKeyboardFocus;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged += OnItemsChanged;
        }

        public void Detach()
        {
            _list.GotKeyboardFocus -= OnGotKeyboardFocus;
            _list.LostKeyboardFocus -= OnLostKeyboardFocus;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged -= OnItemsChanged;
            _restore?.Abort();
            _restore = null;
            _place = null;
        }

        private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // WPF moved focus off a control that was disabled, hidden or taken away with its row, onto one of the
            // control's ancestors or onto the list itself. The control is still where focus belongs, so its place is
            // kept. A row control the user picks has a place of its own, and that one is taken.
            if (_place is not null
                && e.OldFocus is DependencyObject old && !CanTakeFocus(old)
                && e.NewFocus is DependencyObject parked
                && (IsWithin(old, parked) || PlaceOf(_list, parked) is null))
                return;

            _place = e.NewFocus is DependencyObject focused ? PlaceOf(_list, focused) : null;
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is DependencyObject next && IsWithin(next, _list)) return;

            // Focus the user moved to something else in this window stays there. Focus that went to another window, to
            // nothing, or away from a control that was disabled or hidden, keeps its place.
            if (e.OldFocus is DependencyObject old && CanTakeFocus(old)
                && e.NewFocus is DependencyObject elsewhere
                && ReferenceEquals(Window.GetWindow(elsewhere), Window.GetWindow(_list)))
                _place = null;
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_place is null || _restore is not null) return;
            _restore = _list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Restore));
        }

        private void Restore()
        {
            _restore = null;
            if (_place is not { } place) return;

            // A window in the background keeps the place for when it is active again; it does not take focus now.
            if (Window.GetWindow(_list) is not { IsActive: true } window) return;
            if (!_list.IsLoaded || InputManager.Current.MostRecentInputDevice is not KeyboardDevice)
            {
                _place = null;
                return;
            }

            switch (Where(Keyboard.FocusedElement as DependencyObject, window))
            {
                case FocusAt.InTheList:
                    return;
                case FocusAt.Elsewhere:
                    _place = null;
                    return;
            }

            if (TargetFor(_list, place)?.Focus() is true || _list.Focus()) return;

            // No row is left and the list cannot hold focus itself, so focus goes to what comes after it.
            _place = null;
            if (_list.Items.Count == 0 && VisualTreeHelper.GetParent(_list) is UIElement { IsVisible: true })
                _list.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private FocusAt Where(DependencyObject? focused, Window window)
        {
            if (focused is null || ReferenceEquals(focused, window) || ReferenceEquals(focused, _list)) return FocusAt.Lost;

            // It left the screen with its row.
            if (PresentationSource.FromDependencyObject(focused) is null) return FocusAt.Lost;

            if (IsWithin(focused, _list)) return FocusAt.InTheList;

            // One of the list's ancestors, where WPF moves focus from a control that was disabled or hidden.
            return IsWithin(_list, focused) ? FocusAt.Lost : FocusAt.Elsewhere;
        }
    }
}
