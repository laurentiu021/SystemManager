// SysManager · SpeedVerdictAnalyzerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedVerdictAnalyzer"/> — the plain-English reading of a speed-test result.
/// </summary>
/// <remarks>
/// The analyzer is pure and static for the same reason <c>HealthAnalyzer</c> is: the judgement is the
/// part worth testing, and it must be reachable without a network, a timer or a view model. Every case
/// below runs on a constructed <see cref="SpeedTestResult"/>, so nothing here touches the connection.
/// </remarks>
public class SpeedVerdictAnalyzerTests
{
    private static SpeedTestResult Result(double downMbps, string engine = "Ookla") =>
        new(engine, downMbps, UploadMbps: 10, PingMs: 20, Server: "test", CompletedAt: new DateTime(2026, 1, 1));

    // ── The four bands ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5)]
    [InlineData(4.9)]
    public void BelowFiveMbps_IsSlow(double down)
    {
        var verdict = SpeedVerdictAnalyzer.Analyze(Result(down));

        Assert.Equal("Slow connection", verdict.Headline);
        // The detail has to name what will actually struggle, or it is a label without information.
        Assert.Contains("video call", verdict.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(24.9)]
    public void FiveToTwentyFive_IsDecent(double down)
        => Assert.Equal("Decent connection", SpeedVerdictAnalyzer.Analyze(Result(down)).Headline);

    [Theory]
    [InlineData(25)]
    [InlineData(99.9)]
    public void TwentyFiveToOneHundred_IsFast(double down)
        => Assert.Equal("Fast connection", SpeedVerdictAnalyzer.Analyze(Result(down)).Headline);

    [Theory]
    [InlineData(100)]
    [InlineData(940)]
    public void OneHundredAndAbove_IsVeryFast(double down)
        => Assert.Equal("Very fast connection", SpeedVerdictAnalyzer.Analyze(Result(down)).Headline);

    [Fact]
    public void TheBandsAreContiguous_EveryPositiveSpeedGetsAVerdict()
    {
        // Walks the whole range rather than sampling the arms above, so an off-by-one in a threshold
        // (`<` vs `<=`) cannot leave a speed with an empty headline — the boundary values are exactly
        // where a hand-written switch goes wrong.
        for (var down = 0.1; down < 200; down += 0.1)
        {
            var verdict = SpeedVerdictAnalyzer.Analyze(Result(down));
            Assert.False(string.IsNullOrWhiteSpace(verdict.Headline), $"No headline at {down:F1} Mbps");
            Assert.False(string.IsNullOrWhiteSpace(verdict.Detail), $"No detail at {down:F1} Mbps");
        }
    }

    // ── Colour: a slow plan is not a fault ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5)]
    [InlineData(4.9)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(940)]
    public void NoSpeedIsEverColouredAsAFailure(double down)
    {
        // The app's failure colour would tell someone on a cheap rural plan that SysManager found a
        // fault on a working connection. Slow is amber at worst: "this will limit you", never "broken".
        Assert.NotEqual("Danger", SpeedVerdictAnalyzer.Analyze(Result(down)).ColorKey);
    }

    [Fact]
    public void SlowIsAmber_AndFastIsGreen()
    {
        Assert.Equal("Warning", SpeedVerdictAnalyzer.Analyze(Result(2)).ColorKey);
        Assert.Equal("Info", SpeedVerdictAnalyzer.Analyze(Result(10)).ColorKey);
        Assert.Equal("Success", SpeedVerdictAnalyzer.Analyze(Result(50)).ColorKey);
        Assert.Equal("Success", SpeedVerdictAnalyzer.Analyze(Result(500)).ColorKey);
    }

    [Fact]
    public void EveryColourKeyIsOneTheThemeCanResolve()
    {
        // The keys name theme brushes that ThemeService recomputes per preset. A hex literal here would
        // silently reintroduce the dark-calibrated-colour-on-light-theme contrast bug that StatusColors
        // exists to prevent, and HexToBrushConverter would fall back to grey for a typo'd key.
        string[] known = ["Success", "Warning", "Info", "Danger", "TextMuted"];
        foreach (var down in new[] { 0d, 1, 5, 25, 100, 1000 })
            Assert.Contains(SpeedVerdictAnalyzer.Analyze(Result(down)).ColorKey, known);
    }

    // ── States that are not a measurement ───────────────────────────────────────────────────────

    [Fact]
    public void NoResult_SaysSoRatherThanJudging()
    {
        var verdict = SpeedVerdictAnalyzer.Analyze(null);

        Assert.Equal("No test yet", verdict.Headline);
        Assert.Equal("TextMuted", verdict.ColorKey);
        Assert.Empty(verdict.Comparison);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeDownload_IsNotCalledSlow(double down)
    {
        // A cancelled or failed run can leave a zero. Calling that "Slow connection" would be a false
        // accusation about the user's connection based on a measurement that never happened.
        var verdict = SpeedVerdictAnalyzer.Analyze(Result(down));

        Assert.Equal("No speed measured", verdict.Headline);
        Assert.DoesNotContain("Slow", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TextMuted", verdict.ColorKey);
    }

    // ── Comparison against the previous run ─────────────────────────────────────────────────────

    [Fact]
    public void WithNoHistory_ThereIsNoComparisonLine()
        => Assert.Empty(SpeedVerdictAnalyzer.Analyze(Result(50)).Comparison);

    [Theory]
    [InlineData(50, 50)]      // identical
    [InlineData(50, 45)]      // -10%
    [InlineData(50, 60)]      // +20%
    [InlineData(50, 40)]      // exactly -20%
    public void AChangeWithinTheNoiseBand_ReadsAsAboutTheSame(double before, double now)
    {
        var verdict = SpeedVerdictAnalyzer.Analyze(Result(now), Result(before));

        Assert.Contains("About the same", verdict.Comparison, StringComparison.Ordinal);
        // The previous figure is included either way: "about the same as what" is the useful part.
        Assert.Contains($"{before:F0}", verdict.Comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeaningfulDrop_ReadsAsSlowerAndNamesTheOldFigure()
    {
        var verdict = SpeedVerdictAnalyzer.Analyze(Result(30), Result(92));

        Assert.Contains("slower", verdict.Comparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("92", verdict.Comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeaningfulRise_ReadsAsFaster()
    {
        var verdict = SpeedVerdictAnalyzer.Analyze(Result(90), Result(30));

        Assert.Contains("faster", verdict.Comparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30", verdict.Comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroPreviousRun_IsNotComparedAgainst()
    {
        // Dividing by a zero baseline would yield Infinity and report every run as "noticeably faster"
        // than a test that failed.
        Assert.Empty(SpeedVerdictAnalyzer.Analyze(Result(50), Result(0)).Comparison);
    }

    [Fact]
    public void TheComparisonNeverClaimsToMeasureTheUsersPlan()
    {
        // A single test over Wi-Fi says little about the line someone pays for, so the wording must not
        // imply SysManager verified their subscription — that is a complaint to an ISP built on a number
        // the app cannot stand behind.
        string[] forbidden = ["plan", "subscription", "paying", "advertised", "promised"];
        foreach (var verdict in new[]
                 {
                     SpeedVerdictAnalyzer.Analyze(Result(2)),
                     SpeedVerdictAnalyzer.Analyze(Result(50), Result(90)),
                     SpeedVerdictAnalyzer.Analyze(Result(900), Result(10)),
                 })
        {
            var text = $"{verdict.Headline} {verdict.Detail} {verdict.Comparison}";
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
