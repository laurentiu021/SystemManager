// SysManager · FormatHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Helpers;

/// <summary>
/// Shared formatting methods used across multiple ViewModels.
/// Eliminates duplication of common formatting logic.
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB, GB, TB).
    /// <para>Invariant culture, deliberately. A bare interpolated <c>:F1</c> formats with the
    /// user's locale, so on a comma-decimal locale (ro-RO, de-DE) a size printed "1,5 GB" while
    /// <see cref="FormatRate"/> — already invariant — printed "12.4 Mbps" on the same screen.
    /// One decimal separator across the whole app.</para>
    /// <para>A figure that would read 1024.0 once rounded to one decimal takes the next unit: 1,048,575 bytes is
    /// "1.0 MB", where it read "1024.0 KB", and the top of every unit did the same (#2862).</para>
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        var value = bytes / 1024.0;
        var unit = 0;
        var shown = OneDecimal(value);
        while (unit < SizeUnits.Length - 1 && ReadsAtLeast(shown, 1024))
        {
            value /= 1024;
            unit++;
            shown = OneDecimal(value);
        }
        return shown + " " + SizeUnits[unit];
    }

    private static readonly string[] SizeUnits = ["KB", "MB", "GB", "TB"];

    private static string OneDecimal(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a figure, as it is written, reaches <paramref name="next"/>: the rounding that decides is the one the
    /// reader sees.
    /// </summary>
    private static bool ReadsAtLeast(string shown, double next) =>
        double.Parse(shown, NumberStyles.Float, CultureInfo.InvariantCulture) >= next;

    /// <summary>
    /// Formats a byte-per-second rate as a human-readable "12.4 Mbps"-style string. Network speeds
    /// are conventionally shown in BITS per second, so the byte rate is multiplied by 8. Uses
    /// decimal units (bps / Kbps / Mbps / Gbps / Tbps), whole numbers up to Kbps and one decimal
    /// from Mbps up. A zero/negative/NaN rate renders as "0 bps".
    /// <para>A figure that would read 1000 once rounded takes the next unit, as <see cref="FormatSize"/> does at 1024:
    /// 124,950 bytes a second is "1.0 Mbps", where it read "1000 Kbps" (#2862).</para>
    /// </summary>
    public static string FormatRate(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec <= 0) return "0 bps";
        double bits = bytesPerSec * 8.0;
        int u = 0;
        while (bits >= 1000.0 && u < RateUnits.Length - 1) { bits /= 1000.0; u++; }
        var shown = RateFigure(bits, u);
        if (u < RateUnits.Length - 1 && ReadsAtLeast(shown, 1000))
        {
            bits /= 1000.0;
            u++;
            shown = RateFigure(bits, u);
        }
        return $"{shown} {RateUnits[u]}";
    }

    private static readonly string[] RateUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];

    /// <summary>Whole numbers up to Kbps, one decimal from Mbps up.</summary>
    private static string RateFigure(double value, int unit) => unit <= 1
        ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
        : value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Joins names for a sentence: "a", "a and b", "a, b and c", or "" for none.
    /// </summary>
    public static string JoinForSentence(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>
    /// The line a result card shows to name the checks that could not run: "Not checked this time: the disks. The result
    /// above does not cover it." Empty when every check ran.
    /// </summary>
    /// <remarks>
    /// One wording for every card that has to say so — the Quick Tune-Up's and the "Why is it slow?" check's — so a check
    /// that did not run reads the same wherever it is reported (#2501).
    /// </remarks>
    public static string NotChecked(IReadOnlyList<string> notChecked) => notChecked.Count == 0
        ? ""
        : $"Not checked this time: {JoinForSentence(notChecked)}. The result above does not cover "
          + (notChecked.Count == 1 ? "it." : "them.");
}
