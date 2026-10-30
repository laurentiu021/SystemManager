// SysManager · BindingProxy — a view's data context, for what is not in its visual tree
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.Helpers;

/// <summary>
/// Carries a view's data context, through a resource, to something outside its visual tree, such as a
/// <c>DataGridColumn</c>.
/// </summary>
/// <remarks>
/// A column is not in the visual tree and does not inherit a data context, so a binding set on one, whether through
/// <c>RelativeSource</c>, <c>ElementName</c> or the data context, never attaches. Bandwidth Monitor's precise-mode
/// columns hid that way, never hid, and showed every app at 0 bps in the mode the tab starts in (#2705). A Freezable
/// in an element's resources does inherit that element's data context, so the proxy binds
/// <c>Data="{Binding}"</c> there and the column reads <c>{Binding Data.Something, Source={StaticResource …}}</c>.
/// </remarks>
public sealed class BindingProxy : Freezable
{
    /// <summary>The value the proxy carries, usually the data context of the element whose resources hold it.</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    /// <summary>The value the proxy carries, usually the data context of the element whose resources hold it.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc/>
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
