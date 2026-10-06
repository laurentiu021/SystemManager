// SysManager · SizeObserver
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.Helpers;

/// <summary>
/// Pushes an element's laid-out size into two bindable properties, so a view model can lay something out for the
/// space it really has (#1592).
/// </summary>
/// <remarks>
/// <c>ActualWidth</c> and <c>ActualHeight</c> are read-only, and WPF will not bind a read-only property back to the
/// source. So the element sets <see cref="ObservedWidthProperty"/> and <see cref="ObservedHeightProperty"/> on every
/// size change, and those are bound <c>OneWayToSource</c>:
/// <code>h:SizeObserver.Observe="True" h:SizeObserver.ObservedWidth="{Binding Map.Width, Mode=OneWayToSource}"</code>
/// <c>SetCurrentValue</c> rather than <c>SetValue</c>, so the binding stays in place.
/// </remarks>
public static class SizeObserver
{
    /// <summary>Turns observing on or off for the element it is set on.</summary>
    public static readonly DependencyProperty ObserveProperty = DependencyProperty.RegisterAttached(
        "Observe", typeof(bool), typeof(SizeObserver), new PropertyMetadata(false, OnObserveChanged));

    /// <summary>The element's <c>ActualWidth</c>, kept current while observing.</summary>
    public static readonly DependencyProperty ObservedWidthProperty = DependencyProperty.RegisterAttached(
        "ObservedWidth", typeof(double), typeof(SizeObserver), new PropertyMetadata(0d));

    /// <summary>The element's <c>ActualHeight</c>, kept current while observing.</summary>
    public static readonly DependencyProperty ObservedHeightProperty = DependencyProperty.RegisterAttached(
        "ObservedHeight", typeof(double), typeof(SizeObserver), new PropertyMetadata(0d));

    public static bool GetObserve(FrameworkElement element) => (bool)element.GetValue(ObserveProperty);
    public static void SetObserve(FrameworkElement element, bool value) => element.SetValue(ObserveProperty, value);

    public static double GetObservedWidth(FrameworkElement element) => (double)element.GetValue(ObservedWidthProperty);
    public static void SetObservedWidth(FrameworkElement element, double value) => element.SetValue(ObservedWidthProperty, value);

    public static double GetObservedHeight(FrameworkElement element) => (double)element.GetValue(ObservedHeightProperty);
    public static void SetObservedHeight(FrameworkElement element, double value) => element.SetValue(ObservedHeightProperty, value);

    private static void OnObserveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.SizeChanged -= OnSizeChanged;
        if (e.NewValue is not true) return;

        element.SizeChanged += OnSizeChanged;
        Push(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Push((FrameworkElement)sender);

    private static void Push(FrameworkElement element)
    {
        element.SetCurrentValue(ObservedWidthProperty, element.ActualWidth);
        element.SetCurrentValue(ObservedHeightProperty, element.ActualHeight);
    }
}
