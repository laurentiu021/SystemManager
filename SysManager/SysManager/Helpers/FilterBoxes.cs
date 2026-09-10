// SysManager · FilterBoxes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Finds the filter or search box on whichever tab is open, so Ctrl+F can put the caret in it.
/// </summary>
/// <remarks>
/// A tab states which of its text boxes is the filter by what that box binds, and there was no need to
/// invent a marker: 12 of the tabs already bind one of four property names. Keying on the existing
/// binding means no view had to change and no view model gained a member — focusing a control is a View
/// concern, and routing it through a view model would have put UI manipulation where the MVVM rules say
/// it must not go.
/// <para><b>The name list was measured, not assumed.</b> Three names looked like the whole set until a
/// scan of every <c>TextBox</c> whose accessible name mentions filtering or search turned up a twelfth
/// tab — Task Scheduler binds plain <c>Filter</c>. A shortcut that works on eleven tabs and silently not
/// the twelfth is worse than none, because the user learns it is unreliable, so
/// <c>ArchitectureTests.EveryFilterBox_BindsANameCtrlFRecognises</c> holds the list against the views.</para>
/// </remarks>
internal static class FilterBoxes
{
    /// <summary>
    /// The property names a filter or search box binds its <c>Text</c> to, across all of <c>Views/</c>.
    /// </summary>
    internal static readonly string[] BindingPaths = ["FilterText", "SearchText", "SearchQuery", "Filter"];

    /// <summary>True when a <c>Text</c> binding path belongs to a filter or search box.</summary>
    /// <remarks>
    /// Exact match, not a prefix or a contains: <c>FilterTextLength</c> or <c>SearchTextPlaceholder</c>
    /// would be a different property, and a shortcut landing in the wrong box is worse than one that does
    /// nothing. Pure and <c>internal</c> so the decision is testable without a WPF element — creating a
    /// <c>TextBox</c> needs an STA thread, and the part worth pinning is which paths count.
    /// </remarks>
    internal static bool IsFilterBindingPath(string? path) =>
        path is not null && Array.Exists(BindingPaths, p => string.Equals(p, path, StringComparison.Ordinal));

    /// <summary>
    /// The first filter or search box under <paramref name="root"/> in visual-tree order, or null when the
    /// open tab has none.
    /// </summary>
    /// <remarks>
    /// First in tree order rather than a specific one, because a tab with two search boxes — Bulk Installer
    /// has a catalog filter and a winget search — reads top to bottom, and the first is the one a user
    /// pressing Ctrl+F means. Returning null is the normal case on the many tabs that do not filter
    /// anything; the caller then leaves the keypress alone.
    /// <para>Visual tree, not logical: the box sits inside templated cards and panels, and the logical tree
    /// does not cross a <c>ControlTemplate</c>. Collapsed subtrees are skipped, so a filter inside a section
    /// the tab is currently hiding cannot take the caret somewhere invisible.</para>
    /// </remarks>
    internal static TextBox? FindIn(DependencyObject? root)
    {
        if (root is null) return null;

        if (root is TextBox box && IsFilterBindingPath(BoundPath(box))) return box;
        if (root is UIElement { Visibility: not Visibility.Visible }) return null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindIn(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }

        return null;
    }

    /// <summary>The property path a <c>TextBox.Text</c> is bound to, or null when it is not bound.</summary>
    private static string? BoundPath(TextBox box) =>
        BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path?.Path;
}
