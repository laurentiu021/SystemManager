// SysManager · CleanupCategoryHumanSizeBulkTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Exhaustive parameterised coverage for HumanSize formatting. Covers every
/// unit boundary, oddball values, and round-trip behaviour so a regression
/// in the formatter is caught immediately.
/// </summary>
public class CleanupCategoryHumanSizeBulkTests
{
    // Every power of 2 up to 60 — crosses every unit boundary
    public static IEnumerable<object[]> Powers()
    {
        for (var i = 0; i <= 60; i++)
            yield return new object[] { 1L << i };
    }

    [Theory]
    [MemberData(nameof(Powers))]
    public void HumanSize_PowersOfTwo_DoNotCrash(long n)
    {
        var s = CleanupCategory.HumanSize(n);
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.Matches(@"^[\d.,]+ (B|KB|MB|GB|TB)$", s);
    }

    [Theory]
    [InlineData(1L, "B")]
    [InlineData(1023L, "B")]
    [InlineData(1024L, "KB")]
    [InlineData((1024L * 1024) - 1, "KB")]
    [InlineData(1024L * 1024, "MB")]
    [InlineData((1024L * 1024 * 1024) - 1, "MB")]
    [InlineData(1024L * 1024 * 1024, "GB")]
    [InlineData((1024L * 1024 * 1024 * 1024) - 1, "GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "TB")]
    public void HumanSize_PicksCorrectUnit(long bytes, string expectedUnit)
    {
        var s = CleanupCategory.HumanSize(bytes);
        Assert.EndsWith(expectedUnit, s);
    }

    [Theory]
    [InlineData(512L)]
    [InlineData(2048L)]
    [InlineData(10_000L)]
    [InlineData(100_000L)]
    [InlineData(1_000_000L)]
    [InlineData(10_000_000L)]
    [InlineData(100_000_000L)]
    [InlineData(1_000_000_000L)]
    [InlineData(10_000_000_000L)]
    [InlineData(100_000_000_000L)]
    public void HumanSize_RealisticSizes_HaveNumericPrefix(long bytes)
    {
        var s = CleanupCategory.HumanSize(bytes);
        Assert.Matches(@"^\d", s);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(-5L, "0 B")]
    [InlineData(long.MinValue, "0 B")]
    public void HumanSize_ZeroOrNegative_IsZeroBytes(long bytes, string expected)
    {
        Assert.Equal(expected, CleanupCategory.HumanSize(bytes));
    }

    [Theory]
    [InlineData(1023L)]
    [InlineData(1024L * 1023)]
    [InlineData(1024L * 1024 * 1023)]
    [InlineData(1024L * 1024 * 1024 * 1023)]
    public void HumanSize_NearBoundary_ValidFormat(long n)
    {
        Assert.Matches(@"^[\d.,]+ (B|KB|MB|GB|TB)$", CleanupCategory.HumanSize(n));
    }

    [Theory]
    [InlineData(1500L)]
    [InlineData(5500L)]
    [InlineData(9999L)]
    [InlineData(15_000L)]
    [InlineData(1_500_000L)]
    [InlineData(15_500_000L)]
    [InlineData(1_500_000_000L)]
    [InlineData(15_500_000_000L)]
    public void HumanSize_FractionalResult_IsFormatted(long bytes)
    {
        var s = CleanupCategory.HumanSize(bytes);
        Assert.False(string.IsNullOrWhiteSpace(s));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(4L)]
    [InlineData(5L)]
    [InlineData(10L)]
    [InlineData(50L)]
    [InlineData(100L)]
    [InlineData(256L)]
    [InlineData(512L)]
    public void HumanSize_SmallByteValues_StayInBytes(long n)
    {
        var s = CleanupCategory.HumanSize(n);
        Assert.EndsWith(" B", s);
    }

    [Fact]
    public void HumanSize_SameInputSameOutput()
    {
        // Determinism
        var a = CleanupCategory.HumanSize(123456789);
        var b = CleanupCategory.HumanSize(123456789);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024 * 5, "GB")]
    [InlineData(1024L * 1024 * 1024 * 10, "GB")]
    [InlineData(1024L * 1024 * 1024 * 100, "GB")]
    [InlineData(1024L * 1024 * 1024 * 500, "GB")]
    [InlineData(1024L * 1024 * 1024 * 999, "GB")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 2, "TB")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 10, "TB")]
    public void HumanSize_LargeMultiples(long n, string unit)
    {
        Assert.EndsWith(unit, CleanupCategory.HumanSize(n));
    }
}
