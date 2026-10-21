// SysManager · ConvertersTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

public class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // ------- FlexibleBoolToVisibilityConverter -------

    [Theory]
    [InlineData(true, null, Visibility.Visible)]
    [InlineData(false, null, Visibility.Collapsed)]
    [InlineData(true, "Inverse", Visibility.Collapsed)]
    [InlineData(false, "Inverse", Visibility.Visible)]
    public void FlexVis_Bool(bool input, string? param, Visibility expected)
    {
        var c = new FlexibleBoolToVisibilityConverter();
        Assert.Equal(expected, c.Convert(input, typeof(Visibility), param!, Culture));
    }

    [Fact]
    public void FlexVis_NonNullObject_IsVisible()
    {
        var c = new FlexibleBoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert(new object(), typeof(Visibility), null!, Culture));
    }

    [Fact]
    public void FlexVis_Null_IsCollapsed()
    {
        var c = new FlexibleBoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, c.Convert(null!, typeof(Visibility), null!, Culture));
    }

    [Fact]
    public void FlexVis_NullWithInverse_IsVisible()
    {
        var c = new FlexibleBoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert(null!, typeof(Visibility), "Inverse", Culture));
    }
}
