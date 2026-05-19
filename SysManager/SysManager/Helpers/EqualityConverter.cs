// SysManager · EqualityConverter — two-way value equality for radio buttons
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows.Data;

namespace SysManager.Helpers;

/// <summary>
/// Two-way converter that compares a bound value to ConverterParameter.
/// Convert: returns true when value equals parameter (for IsChecked).
/// ConvertBack: returns the parameter when IsChecked becomes true.
/// Usage: IsChecked="{Binding SelectedPlan, Converter={StaticResource IsEqual}, ConverterParameter=balanced}"
/// </summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
            return parameter?.ToString();
        return Binding.DoNothing;
    }
}
