// SysManager · BatteryWearAnalyzer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Turns a battery's capacity history into the sentence the Health figure alone cannot give: whether it is
/// wearing at the rate ordinary use costs (#1513).
/// </summary>
/// <remarks>
/// <para>"Health 87%" with nothing to compare it to cannot be read as fine or failing. What can be read is the
/// change: how much was lost over the last months, against how much a battery loses in ordinary use. Pure and
/// static like <see cref="SpeedVerdictAnalyzer"/>, so every band is testable without a battery.</para>
/// <para>The loss is measured over the last six months, or over the whole history when Windows has less. Each
/// end is the median of up to three entries, because the full-charge figure moves a point or two from week to
/// week on a healthy battery and a single entry at either end would turn that into a trend.</para>
/// <para>The bands describe six months, and a shorter history is scaled to that, so three months that cost what
/// six normally do read as faster than usual. Scaling a short history also scales its noise, so under
/// <see cref="ConfidentHistory"/> a loss above the normal rate is reported as what it is and called too early to
/// judge, rather than as fast wear. The wording is an observation and what helps; it never uses the failure
/// colour and never says the battery is dying.</para>
/// </remarks>
public static class BatteryWearAnalyzer
{
    /// <summary>The shortest history a verdict is given for. Below it the week-to-week noise is all there is.</summary>
    public static readonly TimeSpan MinimumHistory = TimeSpan.FromDays(28);

    /// <summary>The shortest history whose loss is judged against the bands rather than only reported.</summary>
    public static readonly TimeSpan ConfidentHistory = TimeSpan.FromDays(90);

    /// <summary>The period the loss is measured over, and the period the bands describe.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(182);

    /// <summary>Up to this loss in six months is what ordinary use costs.</summary>
    private const double NormalLossPerWindow = 8;

    /// <summary>Above <see cref="NormalLossPerWindow"/> and up to this is a little faster than usual.</summary>
    private const double ALittleFasterLossPerWindow = 15;

    /// <summary>
    /// The verdict for <paramref name="history"/> (oldest first, as <see cref="BatteryReportParser"/> returns it), or
    /// null when it spans less than <see cref="MinimumHistory"/>.
    /// </summary>
    public static BatteryWearVerdict? Assess(IReadOnlyList<BatteryCapacityPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count < 2 || history[^1].Date - history[0].Date < MinimumHistory) return null;

        // The last six months, unless those hold too little to read; then everything Windows kept.
        var since = history[^1].Date - Window;
        List<BatteryCapacityPoint> window = [.. history.Where(p => p.Date >= since)];
        if (window.Count < 2 || window[^1].Date - window[0].Date < MinimumHistory) window = [.. history];

        var span = window[^1].Date - window[0].Date;
        var ends = Math.Clamp(window.Count / 2, 1, 3);
        var lost = Median(window.Take(ends)) - Median(window.TakeLast(ends));
        var perWindow = lost * Window.TotalDays / span.TotalDays;
        var period = DescribePeriod(span);

        if (lost < 0.5)
            return new BatteryWearVerdict("Normal wear",
                $"It has held its capacity over the last {period}. Nothing to do.", StatusColors.Good);

        var amount = $"about {Percent(lost)}";
        if (perWindow <= NormalLossPerWindow)
            return new BatteryWearVerdict("Normal wear",
                $"It has lost {amount} of its capacity in the last {period}, which is about what a battery loses in "
                + "ordinary use. Nothing to do.",
                StatusColors.Good);

        if (span < ConfidentHistory)
            return new BatteryWearVerdict("Too early to tell",
                $"It has lost {amount} of its capacity in the last {period}. That is too short a time to tell normal "
                + "wear from fast wear; after a few months of use this will say which.",
                StatusColors.Info);

        return perWindow <= ALittleFasterLossPerWindow
            ? new BatteryWearVerdict("Wearing a little faster than usual",
                $"It has lost {amount} of its capacity in the last {period}, a little more than ordinary use costs. "
                + "Keeping it away from heat, and from sitting at 100% for days on end, helps it last.",
                StatusColors.Warning)
            : new BatteryWearVerdict("Wearing faster than usual",
                $"It has lost {amount} of its capacity in the last {period}, well over what ordinary use costs. If the "
                + "PC no longer lasts long enough on battery, a replacement will help.",
                StatusColors.Warning);
    }

    /// <summary>
    /// What the card says while <see cref="Assess"/> has no verdict for <paramref name="history"/>: how much Windows has
    /// recorded so far, and when the trend appears.
    /// </summary>
    public static string DescribeTooShort(IReadOnlyList<BatteryCapacityPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var span = history.Count < 2 ? TimeSpan.Zero : history[^1].Date - history[0].Date;
        var recorded = span < TimeSpan.FromDays(7)
            ? "Windows has only just started keeping this battery's history."
            : $"Windows has recorded {DescribePeriod(span)} so far.";
        return recorded + " The trend appears after about a month.";
    }

    private static double Median(IEnumerable<BatteryCapacityPoint> points)
    {
        var sorted = points.Select(p => p.HealthPercent).Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>"6 weeks" under two months, "4 months" under two years, then years.</summary>
    internal static string DescribePeriod(TimeSpan span)
    {
        var days = span.TotalDays;
        if (days < 60) return Plural((int)Math.Round(days / 7), "week");
        if (days < 730) return Plural((int)Math.Round(days / 30.44), "month");
        return Plural((int)Math.Round(days / 365.25), "year");
    }

    private static string Plural(int n, string unit) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + unit + (n == 1 ? "" : "s");

    private static string Percent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + "%";
}
