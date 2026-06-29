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
public sealed record ResourceSample(
    [property: JsonPropertyName("t")] DateTime Timestamp,
    [property: JsonPropertyName("c")] double CpuPercent,
    [property: JsonPropertyName("r")] double RamPercent,
    [property: JsonPropertyName("g")] double? GpuPercent,
    [property: JsonPropertyName("ct")] double? CpuTempC,
    [property: JsonPropertyName("gt")] double? GpuTempC);
