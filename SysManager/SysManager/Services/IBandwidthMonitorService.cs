// SysManager · IBandwidthMonitorService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// How the current bandwidth figures are being measured, so the UI can label them honestly.
/// </summary>
public enum BandwidthMode
{
    /// <summary>
    /// No-admin mode: accurate machine-wide total throughput plus per-process attribution by
    /// active TCP/UDP connections (which apps are talking, to where, how many connections) —
    /// but NOT exact per-process byte rates, which Windows only exposes via an elevated ETW
    /// session. This is the default and needs no elevation or extra privileges.
    /// </summary>
    Connections,

    /// <summary>
    /// Elevated mode: true per-process download/upload byte rates and running totals captured
    /// from a kernel ETW session (needs administrator). Only used when the app is already
    /// elevated and the user opts in; falls back to <see cref="Connections"/> if ETW can't start.
    /// </summary>
    PreciseEtw,
}

/// <summary>
/// A single poll of network activity: the machine-wide total rates for the history graph plus
/// the current per-process rows. Produced by whichever <see cref="IBandwidthMonitorService"/>
/// implementation is active; the ViewModel consumes it identically in both modes.
/// </summary>
public sealed record BandwidthSnapshot(
    BandwidthMode Mode,
    double TotalDownBytesPerSec,
    double TotalUpBytesPerSec,
    IReadOnlyList<ProcessNetworkUsage> Processes);

/// <summary>
/// Samples network usage for the Bandwidth Monitor tab. Two implementations exist: a no-admin
/// connection-attribution source (default) and an elevated ETW source (precise per-app rates).
/// The seam lets the ViewModel and the pure aggregation logic be unit-tested with a substitute,
/// and lets the tab switch modes at runtime without knowing the measurement details (Gate-ARCH).
/// <para>
/// All monitoring is strictly local: no capture leaves the machine, and nothing is written to
/// the system — this is a read-only observer.
/// </para>
/// </summary>
public interface IBandwidthMonitorService : IDisposable
{
    /// <summary>Which measurement mode this instance provides.</summary>
    BandwidthMode Mode { get; }

    /// <summary>
    /// True once the source is initialized and producing data. An ETW source that failed to
    /// start its kernel session reports false so the ViewModel can fall back to the safe source.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts the underlying sampler (opens the ETW session, or primes the interface counters).
    /// Idempotent. Returns false if the source could not start (e.g. ETW needs admin) so the
    /// caller can fall back.
    /// </summary>
    bool Start();

    /// <summary>
    /// Takes one snapshot of current network activity. For rate-based sources this returns the
    /// rates observed since the previous call, so it is expected to be polled on a fixed cadence.
    /// </summary>
    Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default);
}
