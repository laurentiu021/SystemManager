// SysManager · FormatHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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

    // ---------- one decimal separator, whatever the machine's locale ----------
    // FormatSize used a bare interpolated ":F1", which formats with CurrentCulture, while FormatRate
    // was already explicitly invariant. On a comma-decimal locale — ro-RO, de-DE, fr-FR, most of
    // Europe — the same screen showed "1,5 GB" next to "12.4 Mbps". Every assertion above expects a
    // dot, so they were quietly asserting "this test host happens to be en-US".

    [Theory]
    [InlineData("ro-RO")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("")] // invariant
    public void FormatSize_UsesADot_OnEveryLocale(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal("1.5 GB", FormatHelper.FormatSize(1610612736));
            Assert.Equal("1.0 KB", FormatHelper.FormatSize(1L << 10));
            Assert.Equal("512 B", FormatHelper.FormatSize(512));
        });
    }

    [Theory]
    [InlineData("ro-RO")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void SizeAndRate_AgreeOnTheSeparator_OnEveryLocale(string culture)
    {
        WithCulture(culture, () =>
        {
            // The defect was the MISMATCH, so assert the two helpers agree rather than testing them
            // apart: a size and a rate sitting on one screen must not use different separators.
            var size = FormatHelper.FormatSize(1610612736);   // "1.5 GB"
            var rate = FormatHelper.FormatRate(1_550_000);    // "12.4 Mbps"

            Assert.Contains('.', size);
            Assert.Contains('.', rate);
            Assert.DoesNotContain(',', size);
            Assert.DoesNotContain(',', rate);
        });
    }

    /// <summary>
    /// Runs <paramref name="assert"/> with the thread's culture switched, restoring it in a finally so
    /// one test cannot leak a locale into the rest of the run. Deterministic: no ambient state is read.
    /// </summary>
    private static void WithCulture(string culture, Action assert)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture.Length == 0
                ? CultureInfo.InvariantCulture
                : new CultureInfo(culture);
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
