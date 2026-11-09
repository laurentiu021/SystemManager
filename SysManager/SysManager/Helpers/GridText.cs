// SysManager · GridText
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace SysManager.Helpers;

/// <summary>
/// Gives a list's text columns the app's cell style, so a value too long for its column ends in an ellipsis (#3214).
/// </summary>
/// <remarks>
/// A DataGridTextColumn draws its cells with WPF's own style, which neither trims nor wraps, so a name or a path
/// longer than its column was cut at the column's edge, often through a letter, with nothing to say more was there.
/// Columns had been given their own trimming one list at a time, and the next list cut again. Set on a list, this
/// gives every text column still on WPF's style the style it is set to, <c>GridCellText</c> from <c>App.xaml</c>,
/// which trims to an ellipsis and shows the whole value on hover while it is cut, through <see cref="CutText"/>:
/// <code>helpers:GridText.CellStyle="{StaticResource GridCellText}"</code>
/// Every DataGrid has it from the app's DataGrid style, and it reaches the columns a list is given after the style,
/// which is when a view's own columns arrive. A column that needs a look of its own builds its style on
/// <c>GridCellText</c>, which <c>ArchitectureTests</c> holds every view to.
/// </remarks>
public static class GridText
{
    /// <summary>The style the list's text columns draw their cells in, unless a column has one of its own.</summary>
    public static readonly DependencyProperty CellStyleProperty = DependencyProperty.RegisterAttached(
        "CellStyle", typeof(Style), typeof(GridText), new PropertyMetadata(null, OnCellStyleChanged));

    // The list's own handler for its columns, which knows the list: a column added is not yet told which list it is in.
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(GridText), new PropertyMetadata(null));

    public static Style? GetCellStyle(DataGrid grid) => (Style?)grid.GetValue(CellStyleProperty);
    public static void SetCellStyle(DataGrid grid, Style? value) => grid.SetValue(CellStyleProperty, value);

    private static void OnCellStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        if (grid.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler old)
        {
            grid.Columns.CollectionChanged -= old;
            grid.ClearValue(HandlerProperty);
        }
        var previous = e.OldValue as Style;
        if (e.NewValue is not Style style)
        {
            foreach (var column in grid.Columns) Restyle(column, previous, DataGridTextColumn.DefaultElementStyle);
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, change) =>
        {
            foreach (var column in change.NewItems?.OfType<DataGridColumn>() ?? []) Restyle(column, null, style);
        };
        grid.SetValue(HandlerProperty, handler);
        grid.Columns.CollectionChanged += handler;
        foreach (var column in grid.Columns) Restyle(column, previous, style);
    }

    /// <summary>
    /// Gives <paramref name="column"/> <paramref name="style"/> if it is a text column on WPF's own style, or on
    /// <paramref name="previous"/>, the style the list gave it before. A style of the column's own is left alone.
    /// </summary>
    private static void Restyle(DataGridColumn column, Style? previous, Style style)
    {
        if (column is not DataGridTextColumn text) return;
        if (ReferenceEquals(text.ElementStyle, DataGridTextColumn.DefaultElementStyle)
            || (previous is not null && ReferenceEquals(text.ElementStyle, previous)))
            text.ElementStyle = style;
    }
}
