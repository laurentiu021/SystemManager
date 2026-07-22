// SysManager · BandwidthSample
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One point in the total-throughput history graph: the machine-wide download and upload
/// rates (bytes per second) at <see cref="Timestamp"/>. Persisted one-per-line as NDJSON by
/// <c>BandwidthHistoryService</c> (mirroring <c>ResourceHistoryService</c>) so the user can
/// scroll back through the last hour/day/week. Strictly local — nothing leaves the machine.
/// </summary>
public sealed record BandwidthSample(
    DateTime Timestamp,
    double DownBytesPerSec,
    double UpBytesPerSec);
