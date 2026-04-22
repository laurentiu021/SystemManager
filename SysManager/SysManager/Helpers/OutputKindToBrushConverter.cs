// SysManager · OutputKindToBrushConverter
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Models;

namespace SysManager.Helpers;

public class OutputKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            OutputKind.Error => "OutErrorBrush",
            OutputKind.Warning => "OutWarnBrush",
            OutputKind.Info => "OutInfoBrush",
            OutputKind.Verbose => "OutVerboseBrush",
            OutputKind.Debug => "OutDebugBrush",
            OutputKind.Progress => "OutProgressBrush",
            _ => "OutOutputBrush"
        };
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// BooleanToVisibility with optional inversion via ConverterParameter="Inverse".
/// Also treats any non-null object reference as "true" so it can be used to
/// toggle visibility based on a nullable result being populated.
/// </summary>
public class FlexibleBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            bool b => b,
            null => false,
            _ => true
        };
        var invert = parameter as string == "Inverse";
        if (invert) truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToElevationBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? (Brush)new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                                  : new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converts a hex string like "#4CC9F0" to a SolidColorBrush.</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
            catch { /* fall through */ }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
