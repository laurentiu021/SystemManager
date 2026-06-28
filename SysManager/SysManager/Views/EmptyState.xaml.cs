// SysManager · EmptyState
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.Views;

/// <summary>
/// Reusable empty-state placeholder (icon + title + message) for any list or
/// grid that can be empty. Centralizes the idiom so its look can never drift
/// between views. Place it as a sibling of the collection control inside a
/// <see cref="Grid"/> and bind <see cref="UIElement.Visibility"/> to the
/// collection's emptiness via the FlexVis converter with ConverterParameter=Inverse.
/// </summary>
public partial class EmptyState : UserControl
{
    public EmptyState() => InitializeComponent();

    /// <summary>Optional Segoe Fluent Icons glyph shown above the title. Omit for a text-only state.</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    /// <summary>Primary line — what the empty list means (e.g. "No broken shortcuts found").</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    /// <summary>Optional secondary line — how to populate the list (e.g. "Run a scan to check").</summary>
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
