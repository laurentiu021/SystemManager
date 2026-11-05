// SysManager · ResourceSample — one point-in-time reading of system resource usage
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.Json.Serialization;

namespace SysManager.Models;

/// <summary>
/// A single sample of system vitals captured by the always-on resource history sampler.
/// Stored one-per-line as NDJSON. Usage values are percentages (0-100); temperatures are
/// in °C. GPU usage and temperatures are nullable because they are best-effort (NVIDIA-only
/// usage, admin-only CPU temperature). Property names are kept short to bound the on-disk size.
/// </summary>
/// <remarks>
/// The time is an instant with its offset, not a local clock time. Stamped and compared as local times, two samples an
/// hour apart across the autumn clock change read back as the same time and compared equal: a "last hour" view during
/// the repeated hour took in the first pass too, and every prune afterwards interleaved the two passes in the file
/// (#2790). A line written as a local time with its offset, as every line was, reads back as the instant it was.
/// </remarks>
public sealed record ResourceSample(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("c")] double CpuPercent,
    [property: JsonPropertyName("r")] double RamPercent,
    [property: JsonPropertyName("g")] double? GpuPercent,
    [property: JsonPropertyName("ct")] double? CpuTempC,
    [property: JsonPropertyName("gt")] double? GpuTempC);
