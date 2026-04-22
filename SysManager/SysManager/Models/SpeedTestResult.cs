// SysManager · SpeedTestResult
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Result of a speed test run. Values are in Mbps. PingMs is the RTT measured
/// against the test endpoint.
/// </summary>
public record SpeedTestResult(
    string Engine,            // "HTTP" or "Ookla"
    double DownloadMbps,
    double UploadMbps,
    double PingMs,
    string Server,
    DateTime CompletedAt);
