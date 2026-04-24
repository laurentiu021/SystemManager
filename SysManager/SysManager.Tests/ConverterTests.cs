// SysManager · ConverterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for WPF value converters in Helpers.
/// These don't need a running Application — they test the Convert/ConvertBack
/// logic directly.
/// </summary>
public class ConverterTests
{
    // ---------- OutputKindToBrushConverter ----------

    [Theory]
    [InlineData(OutputKind.Error)]
    [InlineData(OutputKind.Warning)]
    [InlineData(OutputKind.Info)]
    [InlineData(OutputKind.Verbose)]
    [InlineData(OutputKind.Debug)]
    [InlineData(OutputKind.Progress)]
    [InlineData(OutputKind.Output)]
    public void OutputKindToBrush_AllKinds_ReturnBrush(OutputKind kind)
    {
        var conv = new OutputKindToBrushConverter();
        var result = conv.Convert(kind, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void OutputKindToBrush_UnknownValue_ReturnsBrush()
    {
        var conv = new OutputKindToBrushConverter();
        var result = conv.Convert(999, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void OutputKindToBrush_ConvertBack_Throws()
    {
        var conv = new OutputKindToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.White, typeof(OutputKind), null!, CultureInfo.InvariantCulture));
    }

    // ---------- FlexibleBoolToVisibilityConverter ----------

    [Fact]
    public void FlexibleBool_True_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_False_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_Null_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_NonNullObject_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("anything", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_True_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(true, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_False_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(false, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_ConvertBack_Throws()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Visibility.Visible, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    // ---------- BoolToElevationBadgeBrushConverter ----------

    [Fact]
    public void ElevationBadge_True_ReturnsGreenBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert(true, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x4C, 0xAF, 0x50), brush.Color);
    }

    [Fact]
    public void ElevationBadge_False_ReturnsGrayBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert(false, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x9E, 0x9E, 0x9E), brush.Color);
    }

    [Fact]
    public void ElevationBadge_NonBool_ReturnsGrayBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert("not a bool", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x9E, 0x9E, 0x9E), brush.Color);
    }

    [Fact]
    public void ElevationBadge_ConvertBack_Throws()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.White, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    // ---------- HexToBrushConverter ----------

    [Fact]
    public void HexToBrush_ValidHex_ReturnsSolidColorBrush()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("#4CC9F0", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x4C, 0xC9, 0xF0), brush.Color);
    }

    [Fact]
    public void HexToBrush_Null_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_EmptyString_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_InvalidHex_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("not-a-color", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_ConvertBack_Throws()
    {
        var conv = new HexToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    // ---------- ProcessStatusToBrushConverter ----------

    [Fact]
    public void StatusBrush_Running_ReturnsGreen()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Running", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), brush.Color);
    }

    [Fact]
    public void StatusBrush_NotResponding_ReturnsRed()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Not responding", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), brush.Color);
    }

    [Fact]
    public void StatusBrush_Unknown_ReturnsGray()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Suspended", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void StatusBrush_Null_ReturnsGray()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void StatusBrush_ConvertBack_Throws()
    {
        var conv = new ProcessStatusToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
