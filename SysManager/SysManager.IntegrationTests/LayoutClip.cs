// SysManager · LayoutClip
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace SysManager.IntegrationTests;

/// <summary>
/// Reads which laid-out elements their parent cuts: the ones given less room than they take.
/// </summary>
/// <remarks>
/// A cut element is still laid out at its full width and then clipped by its parent, so its own size cannot show the
/// cut. The layout clip WPF gives it can, and <see cref="IsCut"/> reads that.
/// </remarks>
internal static class LayoutClip
{
    /// <summary>
    /// The elements under <paramref name="root"/> that are laid out: a collapsed one, and everything inside it, keeps
    /// the sizes of the last pass that showed it, so it is left out with its subtree.
    /// </summary>
    public static IEnumerable<FrameworkElement> LaidOut(DependencyObject root) => LaidOut(root, static _ => true);

    /// <summary>
    /// The elements under <paramref name="root"/> that are laid out, reading inside an element only where
    /// <paramref name="lookInside"/> says to. An element it says no to is still read itself.
    /// </summary>
    public static IEnumerable<FrameworkElement> LaidOut(DependencyObject root, Func<FrameworkElement, bool> lookInside)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is FrameworkElement element)
            {
                yield return element;
                if (!lookInside(element)) continue;
            }
            foreach (var deeper in LaidOut(child, lookInside)) yield return deeper;
        }
    }

    /// <summary>
    /// Whether the room its parent gives <paramref name="element"/> is smaller than the element, beyond rounding.
    /// <paramref name="room"/> is that room, in the element's own coordinates.
    /// </summary>
    public static bool IsCut(FrameworkElement element, out Rect room)
    {
        var clip = LayoutInformation.GetLayoutClip(element);
        room = clip?.Bounds ?? Rect.Empty;
        if (clip is null) return false;
        return room.IsEmpty
            ? element.ActualWidth > 1 || element.ActualHeight > 1
            : room.Width + 1 < element.ActualWidth || room.Height + 1 < element.ActualHeight;
    }

    /// <summary>
    /// Whether the room its parent gives <paramref name="element"/> is narrower than the element, beyond rounding: a
    /// cut across, whatever happens to its height.
    /// </summary>
    public static bool IsCutAcross(FrameworkElement element, out Rect room)
    {
        var clip = LayoutInformation.GetLayoutClip(element);
        room = clip?.Bounds ?? Rect.Empty;
        if (clip is null) return false;
        return room.IsEmpty ? element.ActualWidth > 1 : room.Width + 1 < element.ActualWidth;
    }

    /// <summary>How much of an element its room shows, for a failure message.</summary>
    public static string Shown(Rect room) => room.IsEmpty ? "not at all" : $"{room.Width:F0} x {room.Height:F0}";

    /// <summary>
    /// The element as a failure message names it: its text, its automation name, its own words, or the text of the
    /// first thing inside it.
    /// </summary>
    public static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"the text \"{TextOf(text)}\"",
        _ when AutomationProperties.GetName(element) is { Length: > 0 } name =>
            $"the {element.GetType().Name} \"{name}\"",
        ContentControl { Content: string content } control => $"the {control.GetType().Name} \"{content}\"",
        _ => $"a {element.GetType().Name} holding \"{FirstText(element)}\"",
    };

    private static string TextOf(TextBlock block) =>
        block.Text.Length > 0 ? block.Text : string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));

    private static string FirstText(FrameworkElement element)
    {
        foreach (var inside in LaidOut(element))
        {
            switch (inside)
            {
                case TextBlock block when TextOf(block).Length > 0 && !IsGlyph(block):
                    return TextOf(block);
                case ContentControl { Content: string { Length: > 0 } content }:
                    return content;
            }
        }
        return "";
    }

    /// <summary>An icon drawn from Segoe's symbol fonts, such as a ComboBox's chevron, rather than words.</summary>
    private static bool IsGlyph(TextBlock block) =>
        block.FontFamily.Source.Contains("Segoe Fluent", StringComparison.Ordinal)
        || block.FontFamily.Source.Contains("Segoe MDL2", StringComparison.Ordinal);
}
