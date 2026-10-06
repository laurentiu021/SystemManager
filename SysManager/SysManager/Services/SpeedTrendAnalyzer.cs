// SysManager · SpeedTrendAnalyzer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads one engine's saved speed tests as a trend: what this connection usually gives, and whether a recent
/// run was far below that. It answers "is my internet worse than it used to be, or is it just today?", which
/// the history table holds but cannot show at a glance.
/// </summary>
/// <remarks>
/// <para>Pure and static, like <see cref="SpeedVerdictAnalyzer"/>, for the same reason: the judgement is the part
/// worth testing, so it must be reachable without a chart, a theme or a view model.</para>
/// <para>"Usual" is the median, not the average, so one bad evening does not drag down what the next evening is
/// compared against. And a single result is never a trend: below <see cref="MinimumRuns"/> there is nothing to
/// say, and the chart says so instead of drawing one point as a line.</para>
/// <para>Only runs that measured a download count. A run that came back with nothing is not a slow run, which
/// is the same rule the verdict follows.</para>
/// </remarks>
public static class SpeedTrendAnalyzer
{
    /// <summary>The fewest measured runs a trend is drawn from.</summary>
    public const int MinimumRuns = 2;

    /// <summary>
    /// A run under this fraction of the usual download is "well below" it.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than the verdict's 25% band. That band compares one run with the one before it, and
    /// the verdict card already says when two runs differ by that much; here the question is whether a run is an
    /// outlier against the whole history, and Wi-Fi, the chosen server and whatever else shares the line move a
    /// healthy connection by a quarter all the time. Half is a difference nobody has to squint at.
    /// </remarks>
    private const double WellBelowFraction = 0.5;

    /// <summary>The runs a trend is drawn from: those that measured a download, oldest first.</summary>
    public static IReadOnlyList<SpeedTestResult> Measured(IEnumerable<SpeedTestResult> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return [.. history.Where(r => r.DownloadMbps > 0).OrderBy(r => r.CompletedAt)];
    }

    /// <summary>
    /// The median download of <paramref name="measured"/>, or null with fewer than <see cref="MinimumRuns"/>.
    /// </summary>
    public static double? UsualDownload(IReadOnlyList<SpeedTestResult> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (measured.Count < MinimumRuns) return null;

        var sorted = measured.Select(r => r.DownloadMbps).Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// One plain sentence about <paramref name="measured"/> (oldest first, as <see cref="Measured"/> returns it),
    /// or null with fewer than <see cref="MinimumRuns"/>.
    /// </summary>
    /// <remarks>
    /// Always opens with the usual speed. It says more only when a run was well below it, and what it says depends
    /// on where: several in a row at the end is a change; only the latest is something to re-test; one earlier
    /// run with the latest back to normal is a single slow moment. The order of those checks is the order in
    /// which they matter to someone deciding whether to call their provider.
    /// </remarks>
    public static string? Describe(IReadOnlyList<SpeedTestResult> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (UsualDownload(measured) is not { } usual) return null;

        var opening = $"Usually about {Mbps(usual)}.";
        bool WellBelow(SpeedTestResult r) => r.DownloadMbps < usual * WellBelowFraction;

        // Counted back from the newest run: the runs at the end that were all well below usual.
        var streak = 0;
        for (var i = measured.Count - 1; i >= 0 && WellBelow(measured[i]); i--) streak++;

        if (streak >= 2)
            return $"{opening} The last {streak} tests were all well below that, so this looks like a change "
                   + "rather than one slow moment.";

        if (streak == 1)
        {
            var latest = measured[^1];
            return $"{opening} The latest test, {Mbps(latest.DownloadMbps)} on {When(latest)}, was well below "
                   + "that. Run another to see whether it lasts.";
        }

        var slow = measured.Where(WellBelow).ToList();
        return slow.Count switch
        {
            0 => opening,
            1 => $"{opening} One test in the last {measured.Count}, on {When(slow[0])}, was well below that: "
                 + "one slow moment, not a trend.",
            _ => $"{opening} {slow.Count} of the last {measured.Count} tests were well below that, but the latest "
                 + "is back to normal.",
        };
    }

    private static string Mbps(double value) =>
        value.ToString("F0", CultureInfo.InvariantCulture) + " Mbps";

    private static string When(SpeedTestResult r) =>
        r.CompletedAt.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
}
