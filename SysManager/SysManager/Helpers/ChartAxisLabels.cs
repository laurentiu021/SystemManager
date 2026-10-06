// SysManager · ChartAxisLabels
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Helpers;

/// <summary>
/// Formats a time-axis tick for the charts that plot saved samples over time.
/// </summary>
/// <remarks>
/// <para>One place for the guard below, because every such chart needs it and the defect it prevents only shows
/// when a chart has nothing to plot. Resource History and the Speed Test trend both go through it.</para>
/// <para>The guard used to be <c>v &gt; 0</c>, which admits any positive tick count, and a handful of ticks is a
/// date in the year 1, which formats as <c>01-01 00:00</c>. With no samples the axis has no range to work from,
/// so it laid ticks down near zero and printed that same string across the whole axis (#2371). Hiding the empty
/// chart is the real fix, and it is in each view. This is the second line: a label that cannot be a real sample
/// time renders as nothing, wherever the axis range came from. No sample can predate the app, so any Windows-era
/// date is a safe floor and 2000 is the obvious one.</para>
/// </remarks>
internal static class ChartAxisLabels
{
    /// <summary>The earliest tick count a time axis renders as a date: 2000-01-01.</summary>
    internal static readonly long EarliestPlausibleTicks = new DateTime(2000, 1, 1).Ticks;

    /// <summary>
    /// <paramref name="ticks"/> as a date in <paramref name="format"/>, or an empty string when the value cannot
    /// be a sample time.
    /// </summary>
    internal static string Time(double ticks, string format) =>
        ticks >= EarliestPlausibleTicks && ticks < DateTime.MaxValue.Ticks
            ? new DateTime((long)ticks).ToString(format, CultureInfo.InvariantCulture)
            : "";
}
