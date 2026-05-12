// SysManager · IntGreaterThanZeroConverter
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows.Data;

namespace SysManager.Helpers;

/// <summary>
/// Returns <c>true</c> when the bound integer value is greater than zero.
/// Useful for conditional visibility in DataTriggers.
/// </summary>
[ValueConversion(typeof(int), typeof(bool))]
public sealed class IntGreaterThanZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
