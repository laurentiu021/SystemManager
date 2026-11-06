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
    [InlineData(1048524, "1023.9 KB")]   // the largest size still under 1024.0 once rounded
    public void FormatSize_Kilobytes_ReturnsKB(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSize(bytes));
    }

    [Theory]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(104857600, "100.0 MB")]
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

    // ---------- the top of a unit reads as the next one (#2862) ----------
    // 1,048,575 bytes is 1023.999 KB, which one decimal rounds to 1024.0, so it read "1024.0 KB", and the top of every
    // unit did the same. Rounded, a figure that reaches 1024 belongs to the next unit.

    [Theory]
    [InlineData(1048525, "1.0 MB")]          // the smallest size that rounds to 1024.0 KB
    [InlineData(1048575, "1.0 MB")]          // read "1024.0 KB"
    [InlineData(1073741823, "1.0 GB")]       // read "1024.0 MB"
    [InlineData(1099511627775, "1.0 TB")]    // read "1024.0 GB"
    public void FormatSize_TheTopOfAUnit_ReadsAsTheNextUnit(long bytes, string expected)
        => Assert.Equal(expected, FormatHelper.FormatSize(bytes));

    /// <summary>
    /// Over sizes of every unit, the tops and bottoms of each among them, from a fixed seed: a figure is the size
    /// rounded to one decimal, at least 1 and under 1024 in its unit (TB aside), and a larger size never reads smaller.
    /// </summary>
    [Fact]
    public void FormatSize_OverSizesOfEveryUnit_ReadsTheSizeInTheLargestUnitItReaches()
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var random = new Random(2862);
        var sizes = new List<long>();
        for (var shift = 10; shift <= 40; shift += 10)
        {
            var edge = 1L << shift;
            for (var i = 0; i < 400; i++) sizes.Add(edge + (long)((random.NextDouble() - 0.5) * 0.12 * edge));
        }
        for (var i = 0; i < 4000; i++) sizes.Add((long)Math.Pow(2, random.NextDouble() * 50));
        sizes.Sort();

        var previous = -1.0;
        foreach (var bytes in sizes)
        {
            var text = FormatHelper.FormatSize(bytes);
            var parts = text.Split(' ');
            var unit = Array.IndexOf(units, parts[1]);
            var value = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var inUnit = bytes / Math.Pow(1024, unit);
            Assert.True(unit >= 0, $"{bytes}: \"{text}\" names no unit");
            Assert.True(Math.Abs(inUnit - value) <= (unit == 0 ? 0 : 0.05 + 1e-9),
                $"{bytes}: \"{text}\" is not the size");
            if (unit > 0) Assert.True(value >= 1, $"{bytes}: \"{text}\" belongs to the unit below");
            if (unit < units.Length - 1) Assert.True(value < 1024, $"{bytes}: \"{text}\" belongs to the next unit");
            var shown = value * Math.Pow(1024, unit);
            Assert.True(shown >= previous, $"{bytes}: \"{text}\" reads smaller than the size before it");
            previous = shown;
        }
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

    // ── JoinForSentence ──

    [Fact]
    public void JoinForSentence_OfNothing_IsEmpty()
        => Assert.Equal("", FormatHelper.JoinForSentence(Array.Empty<string>()));

    [Theory]
    [InlineData(new[] { "Skype" }, "Skype")]
    [InlineData(new[] { "Skype", "Maps" }, "Skype and Maps")]
    [InlineData(new[] { "Skype", "Maps", "Mail & Calendar" }, "Skype, Maps and Mail & Calendar")]
    public void JoinForSentence_ReadsAsASentence(string[] items, string expected)
        => Assert.Equal(expected, FormatHelper.JoinForSentence(items));

    // ── NotChecked: one wording for every card that says a check did not run (#1529) ──

    [Fact]
    public void NotChecked_WhenEverythingRan_IsEmpty()
        => Assert.Equal("", FormatHelper.NotChecked(Array.Empty<string>()));

    [Theory]
    [InlineData(new[] { "the disks" }, "Not checked this time: the disks. The result above does not cover it.")]
    [InlineData(new[] { "memory", "uptime" }, "Not checked this time: memory and uptime. The result above does not cover them.")]
    public void NotChecked_NamesWhatDidNotRun_AndSaysTheResultDoesNotCoverIt(string[] notChecked, string expected)
        => Assert.Equal(expected, FormatHelper.NotChecked(notChecked));
}
