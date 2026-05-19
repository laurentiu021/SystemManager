// SysManager · FormatHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

public class FormatHelperTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void FormatSize_Bytes_ReturnsB(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSize(bytes));
    }

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10.0 KB")]
    [InlineData(1048575, "1024.0 KB")]
    public void FormatSize_Kilobytes_ReturnsKB(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSize(bytes));
    }

    [Theory]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(104857600, "100.0 MB")]
    [InlineData(1073741823, "1024.0 MB")]
    public void FormatSize_Megabytes_ReturnsMB(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSize(bytes));
    }

    [Theory]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(1610612736, "1.5 GB")]
    [InlineData(10737418240, "10.0 GB")]
    public void FormatSize_Gigabytes_ReturnsGB(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSize(bytes));
    }

    [Fact]
    public void FormatSize_ExactBoundary_1KB()
    {
        Assert.Equal("1.0 KB", FormatHelper.FormatSize(1L << 10));
    }

    [Fact]
    public void FormatSize_ExactBoundary_1MB()
    {
        Assert.Equal("1.0 MB", FormatHelper.FormatSize(1L << 20));
    }

    [Fact]
    public void FormatSize_ExactBoundary_1GB()
    {
        Assert.Equal("1.0 GB", FormatHelper.FormatSize(1L << 30));
    }
}
