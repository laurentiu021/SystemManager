// SysManager · TimerResolutionStatus
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A snapshot of the Windows multimedia timer resolution. All raw values are in
/// 100-nanosecond units (the unit NtQueryTimerResolution reports); the display
/// helpers convert to milliseconds (value / 10000).
///
/// Note the deliberately counter-intuitive Windows naming: the *finest* achievable
/// resolution is the SMALLEST number (e.g. 5000 = 0.5 ms), and the *coarsest*
/// (default) is the LARGEST (e.g. 156250 ≈ 15.6 ms).
/// </summary>
public sealed record TimerResolutionStatus(
    uint FinestHundredNs,
    uint CoarsestHundredNs,
    uint CurrentHundredNs,
    bool EnabledByApp)
{
    /// <summary>100-nanosecond units → milliseconds.</summary>
    public static double ToMilliseconds(uint hundredNs) => hundredNs / 10000.0;

    public double FinestMs => ToMilliseconds(FinestHundredNs);
    public double CoarsestMs => ToMilliseconds(CoarsestHundredNs);
    public double CurrentMs => ToMilliseconds(CurrentHundredNs);

    public string FinestDisplay => FormatMs(FinestMs);
    public string CoarsestDisplay => FormatMs(CoarsestMs);
    public string CurrentDisplay => FormatMs(CurrentMs);

    /// <summary>
    /// True when the timer is running at (near) its finest achievable resolution —
    /// i.e. a high-resolution timer is in effect. Uses a small tolerance because the
    /// effective value can be a few units off the requested one.
    /// </summary>
    public bool IsHighResolution => CurrentHundredNs <= FinestHundredNs + 500;

    /// <summary>Formats a millisecond value compactly, e.g. "0.5 ms" / "15.625 ms".</summary>
    public static string FormatMs(double ms) => $"{ms:0.###} ms";
}
