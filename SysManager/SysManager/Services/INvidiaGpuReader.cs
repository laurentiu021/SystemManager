// SysManager · INvidiaGpuReader
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// The one owner of NVIDIA's API in the app: it sets it up once, holds the GPU handles, and reads off them
/// under a single lock. Its remaining job in the app is the GPU TEMPERATURE.
/// </summary>
/// <remarks>
/// <para>NVIDIA's <c>Initialize()</c> and <c>GetPhysicalGPUs()</c> are global native calls that are not
/// documented as thread-safe, and three places called them with their own init-once flags: the Dashboard's
/// 300 ms loop, the Resource History sampler's 10 s loop and the temperature read. Two of those start by
/// awaiting the same first system snapshot, so on a normal launch they set the API up at almost the same moment,
/// and a set-up that lost that race switched its own component's GPU reading off for the session (#2829).</para>
/// <para>Load and video memory have since moved to <see cref="IGpuTelemetry"/>, which reads the counters
/// Windows publishes for EVERY vendor — this reader answered only on NVIDIA, so an Intel or AMD adapter showed
/// 0% (#2857). What has NOT moved is the temperature: it is the only GPU temperature this app can read without
/// administrator rights, and its vendor-neutral replacement (<c>D3DKMTQueryAdapterInfo</c>) is an undocumented
/// native surface that needs its own change. This whole type retires when that lands.</para>
/// </remarks>
public interface INvidiaGpuReader
{
    /// <summary>
    /// Each NVIDIA GPU's load, name and dedicated memory, or an empty list when there is none to read.
    /// </summary>
    /// <remarks>
    /// No production caller any more — <see cref="IGpuTelemetry"/> answers this for every vendor (#2857).
    /// Kept while this type still exists for the temperature, because the reader's init-once, handle-caching
    /// and cooldown mechanics — which the temperature read depends on just as much — are exercised through
    /// this method by <c>NvidiaGpuReaderTests</c>. It goes when the type does.
    /// </remarks>
    IReadOnlyList<NvidiaGpuUsage> ReadUsage();

    /// <summary>
    /// Each NVIDIA GPU's temperature, or an empty list when there is none to read. Separate from
    /// <see cref="ReadUsage"/> because reading the thermal sensors is a native call per GPU that the 300 ms
    /// usage poll has no use for.
    /// </summary>
    IReadOnlyList<NvidiaGpuTemperature> ReadTemperatures();
}

/// <summary>One NVIDIA GPU's load and memory.</summary>
/// <param name="Name">The adapter name NVIDIA reports.</param>
/// <param name="UsagePercent">How busy the GPU is, 0 to 100.</param>
/// <param name="MemoryUsedGB">Dedicated video memory in use.</param>
/// <param name="MemoryTotalGB">Dedicated video memory on the card.</param>
public sealed record NvidiaGpuUsage(string Name, double UsagePercent, double MemoryUsedGB, double MemoryTotalGB);

/// <summary>One NVIDIA GPU's temperature.</summary>
/// <param name="Name">The adapter name NVIDIA reports.</param>
/// <param name="TemperatureC">Degrees Celsius, from the first sensor reporting more than zero.</param>
public sealed record NvidiaGpuTemperature(string Name, double TemperatureC);
