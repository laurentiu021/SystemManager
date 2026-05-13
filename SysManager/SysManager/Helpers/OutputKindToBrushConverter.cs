// SysManager · OutputKindToBrushConverter
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };
        var invert = parameter as string == "Inverse";
        if (invert) truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean value. Use for IsEnabled bindings where the source
/// property indicates a "busy" state and the target should be disabled.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class BoolInverterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

public class BoolToElevationBadgeBrushConverter : IValueConverter
{
    private static readonly Brush ElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly Brush NotElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? ElevatedBrush : NotElevatedBrush;
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

/// <summary>
/// Maps process status text ("Running" / "Not responding") to a coloured brush.
/// Green for running, red for not responding, grey for anything else.
/// </summary>
public class ProcessStatusToBrushConverter : IValueConverter
{
    private static readonly Brush RunningBrush = CreateFrozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush NotRespondingBrush = CreateFrozen(Color.FromRgb(0xEF, 0x44, 0x44));

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            if (status.Contains("Not responding", StringComparison.OrdinalIgnoreCase))
                return NotRespondingBrush;
            if (status.Contains("Running", StringComparison.OrdinalIgnoreCase))
                return RunningBrush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
