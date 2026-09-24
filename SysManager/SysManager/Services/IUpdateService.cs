// SysManager · IUpdateService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="UpdateService"/> so ViewModels depend on a mockable seam
/// (Gate-ARCH) instead of the concrete GitHub client. Without it, a test that needs the
/// startup update check to actually run has no choice but to let the unit suite call
/// api.github.com for real — which is both a charter violation (SysManager.Tests takes no
/// system dependencies) and a flake surface, since GitHub's anonymous limit is 60 requests
/// per hour per IP and CI runners share egress addresses. Behind this interface a
/// substitute controls the timing and the failure, so the "couldn't reach GitHub" branches
/// become assertable instead of only reachable by being offline.
/// <para>
/// Every instance member of <see cref="UpdateService"/> is here, not just the two the
/// About tab uses for its startup check: a partial seam would force a consumer to hold
/// both this interface and the concrete class, which is worse than no seam at all. The
/// static surface (<see cref="UpdateService.CurrentVersion"/>, <see cref="UpdateService.Owner"/>,
/// <see cref="UpdateService.IsNewer"/>, <see cref="UpdateService.VerifyAuthenticode"/>) stays
/// static — it reads assembly metadata and the local filesystem, so it needs no double.
/// </para>
/// <para>
/// <see cref="UpdateService.ReleaseInfo"/> deliberately stays nested rather than moving to
/// SysManager.Models the way <see cref="IWingetService"/>'s DTOs did. It has only four
/// references outside its own file, and a consumer of this interface keeps a compile-time
/// dependency on <see cref="UpdateService"/> regardless for the statics above — so promoting
/// the record would widen the diff without buying any decoupling.
/// </para>
/// </summary>
public interface IUpdateService
{
    /// <summary>Why the last call failed, or empty when it succeeded. Never throws instead.</summary>
    string LastError { get; }

    /// <summary>Fetches the newest published release, or null on failure / cancellation.</summary>
    Task<UpdateService.ReleaseInfo?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Fetches the most recent releases newest-first, or an empty list on failure.</summary>
    Task<IReadOnlyList<UpdateService.ReleaseInfo>> GetRecentAsync(int count = 10, CancellationToken ct = default);

    /// <summary>
    /// Downloads the release asset with progress reporting. Returns the path to the
    /// downloaded file, or null on failure / cancellation.
    /// </summary>
    Task<string?> DownloadAsync(
        UpdateService.ReleaseInfo rel,
        IProgress<(long bytesRead, long? total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>Verifies a downloaded file at <paramref name="filePath"/> against the release's published hash.</summary>
    Task<(bool Verified, string? ExpectedHash, string? ActualHash)> VerifyHashAsync(
        UpdateService.ReleaseInfo rel, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Verifies against an already-opened stream, so the caller can hold the handle across
    /// <c>Process.Start</c> and close the TOCTOU window the path-based overload leaves open.
    /// </summary>
    Task<(bool Verified, string? ExpectedHash, string? ActualHash)> VerifyHashAsync(
        UpdateService.ReleaseInfo rel, Stream fileStream, CancellationToken ct = default);
}
