// SysManager · ValueConverters
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Models;

namespace SysManager.Helpers;

public sealed class OutputKindToBrushConverter : IValueConverter
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
/// Truthiness rules: a bool is itself; null is false; a non-empty string is true;
/// a numeric value is true only when non-zero; any other non-null object is true.
/// The numeric rule lets a collection's <c>.Count</c> drive visibility directly
/// (e.g. an empty-state message bound to <c>Items.Count</c> with <c>Inverse</c>),
/// which is the common "show this when the list is empty" pattern.
/// </summary>
public sealed class FlexibleBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            bool b => b,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            sbyte or byte or short or ushort or int or uint or long or ulong => System.Convert.ToInt64(value) != 0,
            float or double or decimal => System.Convert.ToDecimal(value) != 0m,
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
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class BoolToElevationBadgeBrushConverter : IValueConverter
{
    private static readonly Brush ElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly Brush NotElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? ElevatedBrush : NotElevatedBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converts a hex string like "#4CC9F0" to a SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    // Cache frozen brushes by hex value to reduce GC pressure on frequently-updating bindings.
    private static readonly ConcurrentDictionary<string, SolidColorBrush> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return _cache.GetOrAdd(s, static hex =>
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
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps process status text ("Running" / "Not responding") to a coloured brush.
/// Green for running, red for not responding, grey for anything else.
/// </summary>
public sealed class ProcessStatusToBrushConverter : IValueConverter
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

public sealed class SafetyLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeBrush = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush CautionBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));

    static SafetyLevelToBrushConverter()
    {
        SafeBrush.Freeze();
        CautionBrush.Freeze();
        CriticalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => SafeBrush,
            SafetyLevel.Caution => CautionBrush,
            SafetyLevel.Critical => CriticalBrush,
            _ => CriticalBrush
        } : CriticalBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SafetyLevelToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeBg = new(Color.FromArgb(0x20, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush CautionBg = new(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush CriticalBg = new(Color.FromArgb(0x20, 0xEF, 0x44, 0x44));

    static SafetyLevelToBackgroundConverter()
    {
        SafeBg.Freeze();
        CautionBg.Freeze();
        CriticalBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => SafeBg,
            SafetyLevel.Caution => CautionBg,
            SafetyLevel.Critical => CriticalBg,
            _ => CriticalBg
        } : CriticalBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SafetyLevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => "Safe",
            SafetyLevel.Caution => "Caution",
            SafetyLevel.Critical => "Critical",
            _ => "Critical"
        } : "Critical";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
