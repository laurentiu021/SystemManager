// SysManager · BatteryWearAnalyzerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="BatteryWearAnalyzer"/> turns a capacity history into the sentence the Health figure cannot give: whether
/// the battery is wearing at the rate ordinary use costs (#1513).
/// </summary>
public sealed class BatteryWearAnalyzerTests
{
    private static readonly DateTime Start = new(2026, 1, 5);

    /// <summary>A history of (day, percent of new) pairs, with a design capacity that makes each percent exact.</summary>
    private static List<BatteryCapacityPoint> History(params (int Day, double Percent)[] points) =>
        [.. points.Select(p => new BatteryCapacityPoint(Start.AddDays(p.Day), 50000, (long)Math.Round(p.Percent * 500)))];

    [Fact]
    public void LessThanTwoEntries_GetNoVerdict()
    {
        Assert.Null(BatteryWearAnalyzer.Assess([]));
        Assert.Null(BatteryWearAnalyzer.Assess(History((0, 100))));
    }

    [Theory]
    [InlineData(27, false)]
    [InlineData(28, true)]
    public void AVerdict_NeedsFourWeeksOfHistory(int days, bool hasVerdict)
        => Assert.Equal(hasVerdict, BatteryWearAnalyzer.Assess(History((0, 100), (days, 100))) is not null);

    [Fact]
    public void ABatteryThatHeldItsCapacity_IsNormalWear()
    {
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (91, 100), (182, 100)));

        Assert.NotNull(verdict);
        Assert.Equal("Normal wear", verdict.Headline);
        Assert.Equal("It has held its capacity over the last 6 months. Nothing to do.", verdict.Detail);
        Assert.Equal(StatusColors.Good, verdict.ColorKey);
    }

    [Theory]
    [InlineData(96.0, "Normal wear", StatusColors.Good)]
    [InlineData(92.0, "Normal wear", StatusColors.Good)]
    [InlineData(91.5, "Wearing a little faster than usual", StatusColors.Warning)]
    [InlineData(85.0, "Wearing a little faster than usual", StatusColors.Warning)]
    [InlineData(84.5, "Wearing faster than usual", StatusColors.Warning)]
    [InlineData(60.0, "Wearing faster than usual", StatusColors.Warning)]
    public void SixMonthsOfLoss_IsJudgedAgainstWhatOrdinaryUseCosts(double now, string headline, string colour)
    {
        // Up to 8 points in six months is ordinary; up to 15 a little faster; beyond that faster.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (182, now)));

        Assert.Equal(headline, verdict!.Headline);
        Assert.Equal(colour, verdict.ColorKey);
    }

    [Fact]
    public void NormalWear_SaysHowMuch_OverWhatPeriod_AndThatNothingIsNeeded()
    {
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (182, 96)));

        Assert.Equal("It has lost about 4% of its capacity in the last 6 months, which is about what a battery loses "
            + "in ordinary use. Nothing to do.", verdict!.Detail);
    }

    [Fact]
    public void WearingALittleFaster_SaysWhatHelps()
    {
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (182, 88)));

        Assert.Equal("It has lost about 12% of its capacity in the last 6 months, a little more than ordinary use "
            + "costs. Keeping it away from heat, and from sitting at 100% for days on end, helps it last.", verdict!.Detail);
    }

    [Fact]
    public void WearingFaster_SaysWhenAReplacementHelps()
    {
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (182, 82)));

        Assert.Equal("It has lost about 18% of its capacity in the last 6 months, well over what ordinary use costs. "
            + "If the PC no longer lasts long enough on battery, a replacement will help.", verdict!.Detail);
    }

    [Fact]
    public void ALossAboveNormal_InUnderThreeMonths_IsTooEarlyToTell()
    {
        // 4 points in 45 days scales to 16 in six months, but six weeks of week-to-week noise scaled up is not a trend.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (45, 96)));

        Assert.Equal("Too early to tell", verdict!.Headline);
        Assert.Equal(StatusColors.Info, verdict.ColorKey);
        Assert.Equal("It has lost about 4% of its capacity in the last 6 weeks. That is too short a time to tell "
            + "normal wear from fast wear; after a few months of use this will say which.", verdict.Detail);
    }

    [Fact]
    public void ASmallLoss_InUnderThreeMonths_IsStillNormalWear()
    {
        // 1 point in 45 days is 4 in six months: ordinary, whatever the length of the history.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (45, 99)));

        Assert.Equal("Normal wear", verdict!.Headline);
    }

    [Fact]
    public void FromThreeMonths_AShortHistoryIsScaledToSix()
    {
        // 5 points in 91 days is 10 in six months: a little faster than usual, no longer too early to tell.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (91, 95)));

        Assert.Equal("Wearing a little faster than usual", verdict!.Headline);
        Assert.Contains("lost about 5% of its capacity in the last 3 months", verdict.Detail);
    }

    [Fact]
    public void OnlyTheLastSixMonthsAreJudged()
    {
        // A fast first year and a quiet last six months: the verdict is about now.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (100, 90), (218, 80), (300, 79), (400, 78)));

        Assert.Equal("Normal wear", verdict!.Headline);
        Assert.Contains("lost about 2% of its capacity in the last 6 months", verdict.Detail);
    }

    [Fact]
    public void WhenTheLastSixMonthsHoldTooLittle_TheWholeHistoryIsJudged()
    {
        // The last six months hold ten days, too few to read; the whole 310 days are judged. 11 points over ten months
        // is about 6.5 over six: ordinary.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (300, 90), (310, 89)));

        Assert.Equal("Normal wear", verdict!.Headline);
        Assert.Contains("lost about 11% of its capacity in the last 10 months", verdict.Detail);
    }

    [Fact]
    public void OneLowReadingAtTheEnd_IsNotATrend()
    {
        // The full-charge figure moves a point or two from week to week, so each end is a median of up to three. The last
        // entry alone against the first would read 15 points and "a little faster".
        var verdict = BatteryWearAnalyzer.Assess(History(
            (0, 100), (30, 100), (61, 100), (91, 99), (121, 99), (152, 99), (182, 85)));

        Assert.Equal("Normal wear", verdict!.Headline);
        Assert.Contains("lost about 1%", verdict.Detail);
    }

    [Fact]
    public void AnEvenNumberOfEntriesAtAnEnd_AveragesTheMiddleTwo()
    {
        // Four entries: two at each end. 100 and 96 give 98, 90 and 89 give 89.5, so 8.5 points were lost. Either
        // middle entry alone would say 10 or 7.
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (10, 96), (172, 90), (182, 89)));

        Assert.Contains("lost about 9%", verdict!.Detail);
        Assert.Equal("Wearing a little faster than usual", verdict.Headline);
    }

    [Fact]
    public void TheLossIsRoundedHalfAwayFromZero()
    {
        var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (182, 95.5)));

        Assert.Contains("lost about 5%", verdict!.Detail);
    }

    [Fact]
    public void NoVerdict_UsesTheFailureColour_OrCallsTheBatteryFailing()
    {
        // A worn battery is not a fault. Swept over every loss from none to 60 points and six history lengths.
        var checkedVerdicts = 0;
        foreach (var days in new[] { 28, 45, 89, 90, 182, 400 })
        {
            for (var now = 100.0; now >= 40; now -= 0.5)
            {
                var verdict = BatteryWearAnalyzer.Assess(History((0, 100), (days, now)));
                Assert.NotNull(verdict);
                checkedVerdicts++;

                Assert.NotEqual(StatusColors.Bad, verdict.ColorKey);
                foreach (var word in new[] { "dying", "dead", "fail", "broken", "bad" })
                    Assert.DoesNotContain(word, verdict.Headline + " " + verdict.Detail, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Vacuity floor: 121 losses for each of the six lengths.
        Assert.Equal(6 * 121, checkedVerdicts);
    }

    [Theory]
    [InlineData(7, "1 week")]
    [InlineData(45, "6 weeks")]
    [InlineData(59, "8 weeks")]
    [InlineData(60, "2 months")]
    [InlineData(182, "6 months")]
    [InlineData(729, "24 months")]
    [InlineData(730, "2 years")]
    [InlineData(1100, "3 years")]
    public void APeriod_IsSaidInWeeksMonthsOrYears(int days, string expected)
        => Assert.Equal(expected, BatteryWearAnalyzer.DescribePeriod(TimeSpan.FromDays(days)));

    [Fact]
    public void ATooShortHistory_SaysHowMuchWindowsHasRecorded()
        => Assert.Equal("Windows has recorded 2 weeks so far. The trend appears after about a month.",
            BatteryWearAnalyzer.DescribeTooShort(History((0, 100), (14, 99))));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AHistoryOfLessThanAWeek_SaysItHasOnlyJustStarted(int entries)
    {
        var history = History([.. Enumerable.Range(0, entries).Select(i => (i * 3, 100.0))]);

        Assert.Equal("Windows has only just started keeping this battery's history. The trend appears after about a month.",
            BatteryWearAnalyzer.DescribeTooShort(history));
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => BatteryWearAnalyzer.Assess(null!));
        Assert.Throws<ArgumentNullException>(() => BatteryWearAnalyzer.DescribeTooShort(null!));
    }
}
