using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

public class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // ------- HexToBrushConverter -------

    [Theory]
    [InlineData("#4CC9F0")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    public void HexToBrush_Valid_ReturnsBrushWithMatchingColor(string hex)
    {
        var c = new HexToBrushConverter();
        var result = c.Convert(hex, typeof(Brush), null!, Culture);
        var brush = Assert.IsType<SolidColorBrush>(result);

        var expected = (Color)ColorConverter.ConvertFromString(hex);
        Assert.Equal(expected, brush.Color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-color")]
    [InlineData("#GGHHII")]
    [InlineData(42)]
    public void HexToBrush_Invalid_FallsBackToGray(object? input)
    {
        var c = new HexToBrushConverter();
        var result = c.Convert(input!, typeof(Brush), null!, Culture);
        Assert.Same(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_ConvertBack_Throws()
    {
        var c = new HexToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            c.ConvertBack(Brushes.Red, typeof(string), null!, Culture));
    }

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
