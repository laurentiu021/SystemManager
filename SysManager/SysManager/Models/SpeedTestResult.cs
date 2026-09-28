// SysManager · SpeedTestResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Models;

/// <summary>
/// Result of a speed test run. Values are in Mbps. PingMs is the RTT measured
/// against the test endpoint.
/// </summary>
/// <remarks>
/// <see cref="UploadMbps"/> and <see cref="PingMs"/> are null when that part could not be measured: the
/// server refused the upload, or no ping got an answer. Both used to be 0, and 0 ms is a perfect ping, so a
/// network that drops ping showed the best reading there is. A failed download fails the whole test instead.
/// </remarks>
public sealed record SpeedTestResult(
    string Engine,            // "HTTP" or "Ookla"
    double DownloadMbps,
    double? UploadMbps,
    double? PingMs,
    string Server,
    DateTime CompletedAt)
{
    /// <summary>Upload for a result card, e.g. "41.7 Mbps", or "—" when it was not measured.</summary>
    public string UploadDisplay => UploadMbps is { } u ? u.ToString("F1", CultureInfo.InvariantCulture) + " Mbps" : "—";

    /// <summary>Ping for a result card, e.g. "12 ms", or "—" when no ping got an answer.</summary>
    public string PingDisplay => PingMs is { } p ? p.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "—";

    /// <summary>Upload for a history row, whose column header carries the unit: "41.7", or "—".</summary>
    public string UploadMbpsDisplay => UploadMbps is { } u ? u.ToString("F1", CultureInfo.InvariantCulture) : "—";

    /// <summary>Ping for a history row, whose column header carries the unit: "12", or "—".</summary>
    public string PingMsDisplay => PingMs is { } p ? p.ToString("F0", CultureInfo.InvariantCulture) : "—";
}
