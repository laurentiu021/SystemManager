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
    /// </summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1L << 10 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1L << 20 => OneDecimal(bytes, 1L << 10) + " KB",
        < 1L << 30 => OneDecimal(bytes, 1L << 20) + " MB",
        < 1L << 40 => OneDecimal(bytes, 1L << 30) + " GB",
        _ => OneDecimal(bytes, 1L << 40) + " TB"
    };

    private static string OneDecimal(long bytes, long divisor) =>
        (bytes / (double)divisor).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a byte-per-second rate as a human-readable "12.4 Mbps"-style string. Network speeds
    /// are conventionally shown in BITS per second, so the byte rate is multiplied by 8. Uses
    /// decimal units (bps / Kbps / Mbps / Gbps / Tbps), whole numbers up to Kbps and one decimal
    /// from Mbps up. A zero/negative/NaN rate renders as "0 bps".
    /// </summary>
    public static string FormatRate(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec <= 0) return "0 bps";
        double bits = bytesPerSec * 8.0;
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        int u = 0;
        while (bits >= 1000.0 && u < units.Length - 1) { bits /= 1000.0; u++; }
        string value = u <= 1 ? Math.Round(bits).ToString(CultureInfo.InvariantCulture)
                              : bits.ToString("F1", CultureInfo.InvariantCulture);
        return $"{value} {units[u]}";
    }
}
