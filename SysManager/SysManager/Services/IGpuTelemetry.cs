// SysManager · IGpuTelemetry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// Every GPU's load and video memory, from the counters Windows publishes for ALL vendors.
/// </summary>
/// <remarks>
/// <para>GPU usage used to come only from NVIDIA's own API, so on an Intel or AMD adapter the Dashboard showed
/// "GPU 0%" with an empty bar — a legible number that reads as an idle GPU, on most of the laptops this app is
/// aimed at (#2857). Windows publishes the same figures Task Manager draws, for every vendor:
/// <c>\GPU Engine(*)\Utilization Percentage</c> and <c>\GPU Adapter Memory(*)\Dedicated Usage</c>.</para>
/// <para>Nothing here reads a vendor library. The one thing these counters do NOT carry is which physical card
/// each LUID belongs to, which DXGI answers — see <c>GpuTelemetryService</c>.</para>
/// </remarks>
public interface IGpuTelemetry
{
    /// <summary>
    /// Each hardware adapter's load and memory, busiest first, or an empty list when nothing can be measured.
    /// </summary>
    /// <remarks>
    /// Empty rather than zeroed: "no reading" and "an idle GPU" are different answers, and the caller shows
    /// them differently. A software adapter (Microsoft Basic Render Driver) is never listed.
    /// </remarks>
    IReadOnlyList<GpuUsage> ReadUsage();
}

/// <summary>One GPU's load and dedicated video memory.</summary>
/// <param name="Name">The adapter description DXGI reports, e.g. "NVIDIA GeForce RTX 4080".</param>
/// <param name="UsagePercent">
/// How busy the adapter is, 0 to 100: each engine type's instances summed, then the busiest engine type —
/// which is what Task Manager's own GPU column shows.
/// </param>
/// <param name="MemoryUsedGB">Dedicated video memory in use.</param>
/// <param name="MemoryTotalGB">Dedicated video memory on the card, as DXGI reports it.</param>
public sealed record GpuUsage(string Name, double UsagePercent, double MemoryUsedGB, double MemoryTotalGB);
