// SysManager · SpeedTrendAnalyzerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedTrendAnalyzer"/> — one engine's saved speed tests read as a trend (#1499).
/// </summary>
/// <remarks>
/// Every run below is constructed, with fixed dates, so nothing here touches the connection, the clock or the
/// user's history file. The sentences are asserted whole: they are the feature, and a reworded clause is a
/// change someone should see in a diff.
/// </remarks>
public class SpeedTrendAnalyzerTests
{
    private static readonly DateTime Start = new(2026, 9, 26, 20, 0, 0);

    /// <summary>A run <paramref name="day"/> days after <see cref="Start"/>.</summary>
    private static SpeedTestResult Run(double down, int day, double? up = 48) =>
        new("Ookla", down, up, 9, "Bucharest", Start.AddDays(day));

    private static string? Describe(params SpeedTestResult[] runs) =>
        SpeedTrendAnalyzer.Describe(SpeedTrendAnalyzer.Measured(runs));

    // ── What counts ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Measured_DropsRunsThatMeasuredNoDownload_AndOrdersOldestFirst()
    {
        var newest = Run(480, day: 3);
        var failed = Run(0, day: 2);
        var oldest = Run(470, day: 1);

        var measured = SpeedTrendAnalyzer.Measured([newest, failed, oldest]);

        Assert.Equal(new[] { oldest, newest }, measured);
    }

    [Fact]
    public void UsualDownload_NeedsTwoRuns()
    {
        Assert.Null(SpeedTrendAnalyzer.UsualDownload([]));
        Assert.Null(SpeedTrendAnalyzer.UsualDownload([Run(480, 0)]));
        Assert.Equal(2, SpeedTrendAnalyzer.MinimumRuns);
    }

    [Fact]
    public void UsualDownload_IsTheMedian_SoOneBadEveningDoesNotMoveIt()
    {
        // An average of these would be 395; the median stays where the connection usually is.
        Assert.Equal(480, SpeedTrendAnalyzer.UsualDownload([Run(480, 0), Run(120, 1), Run(500, 2), Run(470, 3), Run(490, 4)]));
    }

    [Fact]
    public void UsualDownload_OfAnEvenCount_IsTheMeanOfTheMiddleTwo()
    {
        Assert.Equal(475, SpeedTrendAnalyzer.UsualDownload([Run(500, 0), Run(470, 1), Run(480, 2), Run(100, 3)]));
    }

    // ── The sentence ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_SaysNothing_BelowTwoRuns()
    {
        Assert.Null(Describe());
        Assert.Null(Describe(Run(480, 0)));
        // A failed run is not a second run.
        Assert.Null(Describe(Run(480, 0), Run(0, 1)));
    }

    [Fact]
    public void Describe_OnlyGivesTheUsualSpeed_WhenNothingWasFarBelowIt()
    {
        Assert.Equal("Usually about 480 Mbps.",
            Describe(Run(470, 0), Run(480, 1), Run(500, 2)));
    }

    [Fact]
    public void Describe_CallsOneEarlierSlowRun_ASingleSlowMoment()
    {
        Assert.Equal(
            "Usually about 480 Mbps. One test in the last 5, on 27 Sep, 20:00, was well below that: one slow moment, "
            + "not a trend.",
            Describe(Run(470, 0), Run(212, 1), Run(490, 2), Run(480, 3), Run(500, 4)));
    }

    [Fact]
    public void Describe_AsksForAnotherTest_WhenOnlyTheLatestIsFarBelow()
    {
        Assert.Equal(
            "Usually about 480 Mbps. The latest test, 150 Mbps on 30 Sep, 20:00, was well below that. Run another to "
            + "see whether it lasts.",
            Describe(Run(470, 0), Run(480, 1), Run(500, 2), Run(490, 3), Run(150, 4)));
    }

    [Fact]
    public void Describe_CallsSeveralSlowRunsInARow_AChange()
    {
        // The question the issue starts from: "is my internet worse than it used to be, or is it just today?"
        Assert.Equal(
            "Usually about 475 Mbps. The last 2 tests were all well below that, so this looks like a change rather "
            + "than one slow moment.",
            Describe(Run(470, 0), Run(480, 1), Run(500, 2), Run(490, 3), Run(150, 4), Run(140, 5)));
    }

    [Fact]
    public void Describe_CountsEarlierSlowRuns_WhenTheLatestIsBackToNormal()
    {
        Assert.Equal(
            "Usually about 470 Mbps. 2 of the last 5 tests were well below that, but the latest is back to normal.",
            Describe(Run(470, 0), Run(150, 1), Run(500, 2), Run(140, 3), Run(480, 4)));
    }

    [Fact]
    public void Describe_DoesNotCallExactlyHalfTheUsualSpeed_WellBelow()
    {
        // 240 is half of 480 to the megabit: the line is "under half", so this is a normal, slower run.
        Assert.Equal("Usually about 480 Mbps.", Describe(Run(480, 0), Run(500, 1), Run(470, 2), Run(240, 3), Run(490, 4)));
    }

    [Fact]
    public void Describe_IgnoresAFailedRun_RatherThanCallingItSlow()
    {
        // A run that measured nothing is not a slow run, the same rule the verdict follows.
        Assert.Equal("Usually about 480 Mbps.", Describe(Run(470, 0), Run(480, 1), Run(490, 2), Run(0, 3)));
    }

    [Theory]
    [InlineData("ro-RO")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void Describe_ReadsTheSame_InAnyCulture(string culture)
    {
        // The app writes in English. A comma decimal or a translated month inside an English sentence reads as
        // a glitch, so the numbers and dates are formatted invariantly.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(
                "Usually about 480 Mbps. The latest test, 150 Mbps on 30 Sep, 20:00, was well below that. Run another "
                + "to see whether it lasts.",
                Describe(Run(470, 0), Run(480, 1), Run(490, 2), Run(500, 3), Run(150, 4)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
