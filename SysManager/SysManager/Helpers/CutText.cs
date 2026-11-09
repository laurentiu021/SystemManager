// SysManager · CutText
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Shows a text's own words in its tooltip only while the text is cut short (#3214).
/// </summary>
/// <remarks>
/// A list cell trims a value too long for its column to an ellipsis, and its tooltip carries the whole value, so a
/// cut name or path can still be read. A tooltip on every cell would pop up over numbers and short names that are
/// already whole, so set on a text, this lets the tooltip open only when the tooltip is the text's own words and the
/// text is drawn narrower than they are:
/// <code>helpers:CutText.ToolTip="True"</code>
/// A tooltip that says something else, such as a column's explanation of its value, always opens. Every list's text
/// columns have it from <c>GridCellText</c>, the cell style <see cref="GridText"/> gives them.
/// </remarks>
public static class CutText
{
    /// <summary>Turns showing the tooltip only while the text is cut on or off for the text it is set on.</summary>
    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(bool), typeof(CutText), new PropertyMetadata(false, OnToolTipChanged));

    // A fraction of a pixel: what layout rounding and the two ways of measuring text can differ by.
    private const double Slack = 0.5;

    public static bool GetToolTip(TextBlock text) => (bool)text.GetValue(ToolTipProperty);
    public static void SetToolTip(TextBlock text, bool value) => text.SetValue(ToolTipProperty, value);

    private static void OnToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;

        text.ToolTipOpening -= OnToolTipOpening;
        if (e.NewValue is true) text.ToolTipOpening += OnToolTipOpening;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock text && !ShouldShow(text)) e.Handled = true;
    }

    /// <summary>
    /// Whether <paramref name="text"/>'s tooltip has anything to add: it says something other than the text's own
    /// words, or the text is cut short.
    /// </summary>
    internal static bool ShouldShow(TextBlock text) =>
        text.ToolTip is not string tip || !string.Equals(tip, text.Text, StringComparison.Ordinal) || IsCut(text);

    /// <summary>
    /// Whether <paramref name="text"/> is shown narrower than its words: trimmed to an ellipsis, or laid out whole and
    /// cut by its parent.
    /// </summary>
    internal static bool IsCut(TextBlock text)
    {
        if (string.IsNullOrEmpty(text.Text)) return false;

        var words = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            Brushes.Black, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var shown = text.ActualWidth;
        if (LayoutInformation.GetLayoutClip(text) is { } clip)
            shown = Math.Min(shown, clip.Bounds.IsEmpty ? 0 : clip.Bounds.Width);
        return words.WidthIncludingTrailingWhitespace > shown - text.Padding.Left - text.Padding.Right + Slack;
    }
}
