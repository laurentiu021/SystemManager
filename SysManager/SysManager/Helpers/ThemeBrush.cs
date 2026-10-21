// SysManager · ThemeBrush
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysManager.Helpers;

/// <summary>
/// Paints a brush property in a colour a binding picks: the name of one of the theme's brushes, such as
/// <c>"Success"</c>, or a fixed colour written <c>"#RRGGBB"</c> or <c>"#AARRGGBB"</c>.
/// </summary>
/// <remarks>
/// For the colours that depend on the data and still have to follow the theme: a verdict, a severity dot, a status
/// chip, a console line. They were converters that looked the name up and handed the target the brush they found,
/// and <see cref="Services.ThemeService.Apply"/> stores a new brush under each name rather than changing the old one,
/// so the target kept the old theme's colour until its bound value changed. A console line written on a dark preset
/// stayed near-white on the light card after a switch to Clean Indigo (#2818). Given a name, this sets a resource
/// reference, the thing <c>{DynamicResource}</c> sets, so the property takes each new brush:
/// <code>helpers:ThemeBrush.Foreground="{Binding VerdictColorHex}"</code>
/// A value that is not a name yet goes through a converter that returns one, such as
/// <see cref="SafetyLevelToColorConverter"/>. A name the theme does not have leaves the property at its default, as
/// an unknown <c>{DynamicResource}</c> key does. A fixed colour that does not parse is grey, and no colour at all
/// clears what this set.
/// <para>Foreground goes on a TextBlock, a Run or a control, Background on a Border, a Panel, a control or a
/// TextBlock, Fill on a shape, and BorderBrush on a Border or a control. <c>ArchitectureTests</c> holds the views to
/// that, and to binding no brush property any other way.</para>
/// </remarks>
public static class ThemeBrush
{
    /// <summary>The colour of an element's text.</summary>
    public static readonly DependencyProperty ForegroundProperty = Register("Foreground");

    /// <summary>The colour behind an element.</summary>
    public static readonly DependencyProperty BackgroundProperty = Register("Background");

    /// <summary>The colour inside a shape.</summary>
    public static readonly DependencyProperty FillProperty = Register("Fill");

    /// <summary>The colour of an element's border.</summary>
    public static readonly DependencyProperty BorderBrushProperty = Register("BorderBrush");

    // One frozen brush per fixed colour, shared by every element that shows it, so a list of 200 rows makes one brush.
    private static readonly ConcurrentDictionary<string, Brush> Fixed = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetForeground(DependencyObject element) => (string?)element.GetValue(ForegroundProperty);

    public static void SetForeground(DependencyObject element, string? value) =>
        element.SetValue(ForegroundProperty, value);

    public static string? GetBackground(DependencyObject element) => (string?)element.GetValue(BackgroundProperty);

    public static void SetBackground(DependencyObject element, string? value) =>
        element.SetValue(BackgroundProperty, value);

    public static string? GetFill(DependencyObject element) => (string?)element.GetValue(FillProperty);

    public static void SetFill(DependencyObject element, string? value) => element.SetValue(FillProperty, value);

    public static string? GetBorderBrush(DependencyObject element) => (string?)element.GetValue(BorderBrushProperty);

    public static void SetBorderBrush(DependencyObject element, string? value) =>
        element.SetValue(BorderBrushProperty, value);

    private static DependencyProperty Register(string name) => DependencyProperty.RegisterAttached(
        name, typeof(string), typeof(ThemeBrush), new PropertyMetadata(null, OnColorChanged));

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var target = TargetOf(d.GetType(), e.Property);
        var color = e.NewValue as string;

        if (string.IsNullOrWhiteSpace(color)) d.ClearValue(target);
        else if (color.StartsWith('#')) d.SetValue(target, FixedBrush(color));
        else if (d is FrameworkElement element) element.SetResourceReference(target, color);
        else if (d is FrameworkContentElement content) content.SetResourceReference(target, color);
    }

    /// <summary>
    /// The property of an element of type <paramref name="element"/> that <paramref name="attached"/> paints.
    /// </summary>
    /// <remarks>
    /// One property for every element that has it, because WPF shares them: a TextBlock's, a Run's and a control's
    /// Foreground are all <see cref="TextElement.ForegroundProperty"/>, and a Border's, a Panel's and a control's
    /// Background and BorderBrush are <see cref="Panel.BackgroundProperty"/> and
    /// <see cref="Border.BorderBrushProperty"/>. A TextBlock's Background is the one its text has,
    /// <see cref="TextElement.BackgroundProperty"/>.
    /// </remarks>
    internal static DependencyProperty TargetOf(Type element, DependencyProperty attached)
    {
        if (attached == ForegroundProperty) return TextElement.ForegroundProperty;
        if (attached == FillProperty) return Shape.FillProperty;
        if (attached == BorderBrushProperty) return Border.BorderBrushProperty;
        return typeof(TextBlock).IsAssignableFrom(element) || typeof(TextElement).IsAssignableFrom(element)
            ? TextElement.BackgroundProperty
            : Panel.BackgroundProperty;
    }

    private static Brush FixedBrush(string color) => Fixed.GetOrAdd(color, static hex =>
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    });
}
