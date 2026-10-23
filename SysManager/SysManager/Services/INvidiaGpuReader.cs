// SysManager · INvidiaGpuReader
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// The one owner of NVIDIA's API in the app: it sets it up once, holds the GPU handles, and reads usage, memory and
/// temperature off them under a single lock.
/// </summary>
/// <remarks>
/// NVIDIA's <c>Initialize()</c> and <c>GetPhysicalGPUs()</c> are global native calls that are not documented as
/// thread-safe, and three places called them with their own init-once flags: the Dashboard's 300 ms loop, the
/// Resource History sampler's 10 s loop and the temperature read. Two of those start by awaiting the same first
/// system snapshot, so on a normal launch they set the API up at almost the same moment, and a set-up that lost that
/// race switched its own component's GPU reading off for the session (#2829).
/// </remarks>
public interface INvidiaGpuReader
{
    /// <summary>
    /// Each NVIDIA GPU's load, name and dedicated memory, or an empty list when there is none to read.
    /// </summary>
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
