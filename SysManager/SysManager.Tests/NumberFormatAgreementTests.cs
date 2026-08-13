// SysManager · NumberFormatAgreementTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Pins the separator agreement between <c>FormatHelper</c> and every number the app prints inline.
/// <para>v1.64.16 made <c>FormatHelper</c> invariant but left the interpolation holes on
/// <c>CurrentCulture</c>, so the fix produced a MIXED screen rather than a consistent one. Measured on
/// ro-RO for one 1.5 GB value: the helper rendered <c>1.5 GB</c> while the hole beside it rendered
/// <c>1,5 GB</c> — two decimal marks in the same sentence, which is the inconsistency v1.64.16 set out
/// to remove.</para>
/// <para>These tests assert AGREEMENT rather than testing each side alone: the defect was never "this
/// number looks wrong", it was "these two numbers disagree". A test on one side in isolation would have
/// passed throughout.</para>
/// </summary>
public sealed class NumberFormatAgreementTests
{
    /// <summary>ro-RO/de-DE use a comma decimal and dot group; fr-FR/fi-FI use a space group.</summary>
    public static TheoryData<string> CommaCultures() => new() { "ro-RO", "de-DE", "fr-FR", "fi-FI", "en-US", "" };

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void BootRecord_SecondsUseTheSameSeparatorAsTheHelpers(string culture)
    {
        // Millisecond values chosen so the tenth is unambiguous. 12_250 would render "12.2", not
        // "12.3": F1 rounds to even on an exact .x5 tie, and this test is about the SEPARATOR, so a
        // rounding boundary in the input would only obscure what it is pinning.
        var record = new BootRecord(new DateTime(2026, 8, 4, 13, 45, 30, DateTimeKind.Utc), 30_500, 12_300, 18_700);

        WithCulture(culture, () =>
        {
            Assert.Equal("30.5 s", record.BootSecondsDisplay);
            Assert.Equal("12.3 s", record.MainPathDisplay);
            Assert.Equal("18.7 s", record.PostBootDisplay);
        });
    }

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void ADecimalFromAHole_AgreesWithADecimalFromTheHelper(string culture)
    {
        // The defect in one assertion: one screen, one value, two code paths. Before this change the
        // helper said "1.5 GB" and the hole said "1,5 GB" on every comma locale.
        var record = new BootRecord(new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc), 1_500, 1_500, 1_500);

        WithCulture(culture, () =>
        {
            var fromHelper = FormatHelper.FormatSize(1_610_612_736);   // "1.5 GB"
            var fromHole = record.BootSecondsDisplay;                  // "1.5 s"

            var helperMark = fromHelper.First(char.IsPunctuation);
            var holeMark = fromHole.First(char.IsPunctuation);
            Assert.Equal(helperMark, holeMark);
            Assert.Equal('.', holeMark);
        });
    }

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void CleanupCategory_GroupsThousandsTheSameEverywhere(string culture)
    {
        // N0 grouping differs three ways: "1,610" invariant, "1.610" on ro-RO/de-DE, "1 610" on
        // fr-FR/fi-FI — and a space group separator is a non-breaking space, not a plain one, so it
        // does not even round-trip through a copy-paste.
        var category = new CleanupCategory
        {
            Name = "Temporary files",
            Description = "d",
            Paths = [],
            TotalSizeBytes = 1024,
            FileCount = 1610,
            SkippedCount = 0,
        };

        WithCulture(culture, () => Assert.Contains("1,610", category.CountDisplay));
    }

    [Fact]
    public void TheCommaCultures_ReallyDoDisagree_AboutTheseNumbers()
    {
        // Guards the guard. If a future runtime reported a dot decimal for ro-RO, every test above
        // would pass while proving nothing — so assert the hazard exists on this machine.
        var romanian = new CultureInfo("ro-RO");
        var french = new CultureInfo("fr-FR");

        Assert.Equal("1,5", 1.5.ToString("F1", romanian));
        Assert.Equal("1.5", 1.5.ToString("F1", CultureInfo.InvariantCulture));
        Assert.NotEqual("1,610", 1610.ToString("N0", french));
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
