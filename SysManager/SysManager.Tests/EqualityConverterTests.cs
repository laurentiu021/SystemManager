// SysManager · EqualityConverterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows.Data;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EqualityConverter"/>.
/// </summary>
public class EqualityConverterTests
{
    private readonly EqualityConverter _sut = new();

    [Theory]
    [InlineData("balanced", "balanced", true)]
    [InlineData("high", "high", true)]
    [InlineData("ultimate", "ultimate", true)]
    [InlineData("balanced", "high", false)]
    [InlineData("high", "ultimate", false)]
    [InlineData("", "balanced", false)]
    public void Convert_ComparesValueToParameter(string value, string parameter, bool expected)
    {
        var result = _sut.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NullValue_ReturnsFalse()
    {
        var result = _sut.Convert(null, typeof(bool), "balanced", CultureInfo.InvariantCulture);
        Assert.False((bool)result!);
    }

    [Fact]
    public void Convert_NullParameter_ReturnsFalse()
    {
        var result = _sut.Convert("balanced", typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result!);
    }

    [Fact]
    public void Convert_BothNull_ReturnsFalse()
    {
        var result = _sut.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.False((bool)result!);
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("high")]
    [InlineData("ultimate")]
    public void ConvertBack_WhenTrue_ReturnsParameter(string parameter)
    {
        var result = _sut.ConvertBack(true, typeof(string), parameter, CultureInfo.InvariantCulture);
        Assert.Equal(parameter, result);
    }

    [Fact]
    public void ConvertBack_WhenFalse_ReturnsDoNothing()
    {
        var result = _sut.ConvertBack(false, typeof(string), "balanced", CultureInfo.InvariantCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void ConvertBack_WhenNull_ReturnsDoNothing()
    {
        var result = _sut.ConvertBack(null, typeof(string), "balanced", CultureInfo.InvariantCulture);
        Assert.Equal(Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_IsCaseSensitive()
    {
        var result = _sut.Convert("Balanced", typeof(bool), "balanced", CultureInfo.InvariantCulture);
        Assert.False((bool)result!);
    }
}
