// SysManager · CpuLoadSampler
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// CPU load over one caller's own intervals: the busy share of the ticks since that caller's previous sample.
/// </summary>
/// <remarks>
/// <see cref="SystemInfoService"/> kept one previous sample for every caller and replaced it on every call. The
/// Dashboard samples every 300 ms, Resource History every 10 seconds and the tray tooltip every minute, so each reading
/// covered the time since whichever of them sampled last: with the Dashboard open, a Resource History point was the
/// load of the last fraction of a second, and every Resource History or tray sample cut one Dashboard interval down to
/// a few clock ticks (#2790). Each caller that shows a load now holds one of these, from
/// <see cref="SystemInfoService.NewCpuSampler"/>, and no call made with another, or with none, moves its baseline.
/// <para>Three cases answer with something honest rather than zero, because zero is a legible number that means "idle
/// machine":</para>
/// <list type="bullet">
///   <item>the syscall failed: the last known value stands instead of a 0 flashing into the chart;</item>
///   <item>no previous sample at all, the seeding syscall having failed too: the since-boot average, which is what a
///   delta measured from zero actually is;</item>
///   <item>two samples inside one timer tick, so the delta is 0 ticks wide: the last value stands, since 0/0 is not 0%
///   busy.</item>
/// </list>
/// </remarks>
public sealed class CpuLoadSampler
{
    private readonly Func<SystemInfoService.SystemTimes?> _systemTimes;
    private readonly Action<double>? _measured;
    private readonly Lock _lock = new();
    private SystemInfoService.SystemTimes? _previous;
    private double _last = double.NaN;

    /// <summary>Seeds the baseline now, so the first sample measures the interval from here to it.</summary>
    /// <param name="systemTimes">Idle, kernel and user tick totals since boot, or null when the syscall failed.</param>
    /// <param name="measured">Told each load measured over a real interval, so a capture with no sampler of its own
    /// can show the latest one without taking a sample.</param>
    internal CpuLoadSampler(Func<SystemInfoService.SystemTimes?> systemTimes, Action<double>? measured = null)
    {
        _systemTimes = systemTimes;
        _measured = measured;
        _previous = systemTimes();
    }

    /// <summary>The busy share of the ticks since this sampler's previous sample, as a percentage.</summary>
    public double Sample()
    {
        lock (_lock)
        {
            var current = _systemTimes();
            if (current is null) return double.IsNaN(_last) ? 0 : _last;

            var now = current.Value;
            var previous = _previous;
            _previous = now;

            if (previous is { } was)
            {
                // Unsigned subtraction: these counters only ever advance, but a spurious backwards step would wrap to
                // an enormous delta and produce a nonsense percentage, so the arithmetic is done after an explicit
                // ordering check.
                if (now.Idle >= was.Idle && now.Kernel >= was.Kernel && now.User >= was.User)
                {
                    // Kernel time includes idle time, so kernel and user together are every tick of the interval.
                    var totalTicks = (now.Kernel - was.Kernel) + (now.User - was.User);
                    if (totalTicks > 0)
                    {
                        _last = BusyPercent(now.Idle - was.Idle, totalTicks);
                        _measured?.Invoke(_last);
                        return _last;
                    }
                }
            }

            if (double.IsNaN(_last)) _last = BusyPercent(now.Idle, now.Kernel + now.User);
            return _last;
        }
    }

    /// <summary>Busy share of <paramref name="total"/> ticks, clamped to a percentage.</summary>
    internal static double BusyPercent(ulong idle, ulong total)
        => total == 0 ? 0 : Math.Clamp((1.0 - (double)idle / total) * 100.0, 0, 100);
}
