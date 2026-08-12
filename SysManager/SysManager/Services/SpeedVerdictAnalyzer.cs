// SysManager · SpeedVerdictAnalyzer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Turns a speed-test result into a plain-English answer to the question the user actually opened the
/// tab with: not "what is my speed" but "is that any good".
/// </summary>
/// <remarks>
/// <para>Pure and static, like <see cref="HealthAnalyzer"/> — same reason: the judgement is the part
/// worth testing, so it must be reachable without a network, a timer, or a view model.</para>
/// <para>The thresholds are deliberately broad and described in things a person recognises (a video
/// call, HD video, 4K, several devices) rather than in numbers, because a number is what the tab
/// already showed and what the user could not interpret. They are rough guidance about the connection
/// as measured right now, and the wording never claims to have measured the plan someone pays for —
/// a single test over Wi-Fi says little about the line itself.</para>
/// </remarks>
public static class SpeedVerdictAnalyzer
{
    /// <summary>Below this, a video call is a coin flip. Roughly what one HD stream needs.</summary>
    private const double SlowMbps = 5;

    /// <summary>Comfortable for HD on a couple of devices, short of 4K plus a household.</summary>
    private const double OkMbps = 25;

    /// <summary>4K and several devices at once.</summary>
    private const double FastMbps = 100;

    /// <summary>
    /// A run has to differ from the previous one by more than this fraction before it is called out as
    /// a change. Speed tests vary between runs on identical lines — Wi-Fi, the chosen server, whatever
    /// else is using the connection — so a tighter band would report noise as news, every time.
    /// </summary>
    private const double MeaningfulChange = 0.25;

    /// <summary>
    /// The verdict for <paramref name="result"/>, optionally compared against the previous run on the
    /// same engine.
    /// </summary>
    /// <param name="result">The run just completed. Null yields the waiting state.</param>
    /// <param name="previous">
    /// The most recent earlier run on the same engine, or null when there is no history. Comparing
    /// across engines would be meaningless — the HTTP and Ookla tests measure differently — so the
    /// caller is responsible for passing a same-engine run.
    /// </param>
    public static SpeedVerdict Analyze(SpeedTestResult? result, SpeedTestResult? previous = null)
    {
        if (result is null)
            return new SpeedVerdict("No test yet", "Press Start to measure your connection.", StatusColors.Neutral, "");

        var down = result.DownloadMbps;

        // A failed or aborted run can leave a zero. Saying "slow" about a measurement that did not
        // happen would be a false accusation about the user's connection.
        if (down <= 0)
            return new SpeedVerdict(
                "No speed measured",
                "The test did not return a download speed. Try again, or use the other test engine.",
                StatusColors.Neutral, "");

        // Note what is NOT here: StatusColors.Bad. A slow result is not an error. Someone on a cheap
        // rural plan would see the app's failure colour on a working connection and reasonably conclude
        // it had found a fault. Amber at worst, meaning "this will limit you", never "something is broken".
        var (headline, detail, colour) = down switch
        {
            < SlowMbps => ("Slow connection",
                "Fine for email and browsing, but video calls and HD video will struggle — especially with " +
                "someone else using the connection at the same time.",
                StatusColors.Warning),
            < OkMbps => ("Decent connection",
                "Comfortable for HD streaming on one or two devices, and for video calls.",
                StatusColors.Info),
            < FastMbps => ("Fast connection",
                "Handles 4K streaming and several devices at once.",
                StatusColors.Good),
            _ => ("Very fast connection",
                "More than enough for anything a home connection is normally asked to do.",
                StatusColors.Good),
        };

        return new SpeedVerdict(headline, detail, colour, CompareWithPrevious(down, previous));
    }

    /// <summary>
    /// One sentence putting this run next to the last one, or empty when there is nothing to compare.
    /// This is the part that turns a single number into evidence someone can take to their provider.
    /// </summary>
    private static string CompareWithPrevious(double down, SpeedTestResult? previous)
    {
        if (previous is null || previous.DownloadMbps <= 0) return "";

        var before = previous.DownloadMbps;
        var change = (down - before) / before;

        if (Math.Abs(change) <= MeaningfulChange)
            return $"About the same as your last test ({before:F0} Mbps).";

        return change < 0
            ? $"Noticeably slower than your last test (was {before:F0} Mbps)."
            : $"Noticeably faster than your last test (was {before:F0} Mbps).";
    }
}
