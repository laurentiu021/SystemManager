// SysManager · BandwidthFormat — pure, unit-testable bandwidth math & formatting
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// Pure domain helpers for the Bandwidth Monitor: per-second delta math, remote-port summary, and
/// the threshold check. No WPF, no P/Invoke, no ETW — so every branch is unit-tested
/// deterministically (Gate-ARCH: the logic is separated from the OS-touching sources). Human
/// rate/size formatting lives in <see cref="FormatHelper"/> (the single source of truth reachable
/// from Models too); the thin wrappers here just forward to it for callers already using this type.
/// </summary>
public static class BandwidthFormat
{
    /// <summary>Formats a byte-per-second rate in bits/sec units. Delegates to <see cref="FormatHelper.FormatRate"/>.</summary>
    public static string FormatRate(double bytesPerSec) => FormatHelper.FormatRate(bytesPerSec);

    /// <summary>Formats a byte total in binary units. Delegates to <see cref="FormatHelper.FormatSize"/>.</summary>
    public static string FormatBytes(long bytes) => FormatHelper.FormatSize(bytes);

    /// <summary>
    /// Computes a non-negative per-second rate from two cumulative counter readings.
    /// Guards against the counter going backwards (interface reset / 32-bit wrap surfaced by the
    /// OS) by clamping a negative delta to zero, and against a zero/negative elapsed time by
    /// returning zero — so a bad reading yields 0, never a negative or infinite rate.
    /// </summary>
    public static double RatePerSecond(long previous, long current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return 0;
        long delta = current - previous;
        if (delta < 0) return 0;
        return delta / elapsedSeconds;
    }

    /// <summary>
    /// Summarizes a set of remote ports into a short, stable, de-duplicated string like
    /// "443, 5223, 27015" (most-frequent first, capped) for the row's context column. Ports at
    /// or below zero are ignored. Returns an empty string when there are none.
    /// </summary>
    public static string SummarizePorts(IEnumerable<int> remotePorts, int max = 4)
    {
        if (max < 1) max = 1;
        var counts = new Dictionary<int, int>();
        foreach (var p in remotePorts)
        {
            if (p <= 0) continue;
            counts[p] = counts.TryGetValue(p, out var c) ? c + 1 : 1;
        }
        if (counts.Count == 0) return "";
        var ordered = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(max)
            .Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture));
        var joined = string.Join(", ", ordered);
        return counts.Count > max ? joined + ", …" : joined;
    }

    /// <summary>
    /// True when a total rate (bytes/sec) exceeds the given threshold expressed in Mbps. A
    /// threshold at or below zero disables the alert (always false). Centralized so the VM and
    /// its tests agree on exactly where the line is.
    /// </summary>
    public static bool ExceedsThresholdMbps(double bytesPerSec, double thresholdMbps)
    {
        if (thresholdMbps <= 0) return false;
        double mbps = bytesPerSec * 8.0 / 1_000_000.0;
        return mbps > thresholdMbps;
    }
}
