// SysManager · IntGreaterThanZeroConverterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="IntGreaterThanZeroConverter"/>.
/// </summary>
public class IntGreaterThanZeroConverterTests
{
    private readonly IntGreaterThanZeroConverter _sut = new();

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(-100, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(100, true)]
    public void Convert_ReturnsExpected(int input, bool expected)
    {
        var result = _sut.Convert(input, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NonInt_ReturnsFalse()
    {
        var result = _sut.Convert("hello", typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Convert_Null_ReturnsFalse()
    {
        var result = _sut.Convert(null!, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _sut.ConvertBack(true, typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
