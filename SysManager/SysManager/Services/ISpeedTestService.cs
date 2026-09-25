// SysManager · ISpeedTestService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The two speed-test engines, behind a seam. The Dashboard's quick test runs through it, so a view model
/// tested against a substitute never downloads fifty megabytes from Cloudflare.
/// </summary>
public interface ISpeedTestService
{
    /// <summary>Measures ping, download and upload against Cloudflare over HTTP.</summary>
    Task<SpeedTestResult> RunHttpAsync(IProgress<(int Percent, string Message)>? progress, CancellationToken ct);

    /// <summary>Runs Ookla's own speedtest CLI, downloading it on first use.</summary>
    Task<SpeedTestResult> RunOoklaAsync(
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct, int? serverId = null);
}
