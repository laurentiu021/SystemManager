// SysManager · TimerResolutionService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Controls the Windows multimedia timer resolution via the ntdll
/// <c>NtQueryTimerResolution</c> / <c>NtSetTimerResolution</c> APIs.
///
/// A finer timer resolution (e.g. 0.5 ms instead of the ~15.6 ms default) reduces
/// input latency for games at the cost of higher power draw. The request is a
/// per-process contribution that Windows reverts when this process exits, so it is
/// fully reversible and needs no admin rights.
///
/// IMPORTANT lifetime caveat (Windows 10 2004+ / Windows 11): a request is honored
/// for the requesting process but is no longer a system-wide global override, and on
/// Windows 11 it can stop being honored while this app's window is occluded/minimized.
/// We therefore always re-query the EFFECTIVE resolution after a set rather than
/// trusting the requested value.
/// </summary>
public sealed partial class TimerResolutionService : ITimerResolutionService
{
    private bool _enabledByApp;

    /// <summary>0.5 ms expressed in 100-nanosecond units — the common gaming target.</summary>
    public const uint HalfMilliInHundredNs = 5000;

    /// <summary>Read the achievable range and the resolution currently in effect.</summary>
    public TimerResolutionStatus Query()
    {
        try
        {
            int status = NativeMethods.NtQueryTimerResolution(out uint finest, out uint coarsest, out uint current);
            if (status != NativeMethods.StatusSuccess)
            {
                Log.Debug("NtQueryTimerResolution failed: NTSTATUS 0x{Status:X8}", status);
                return new TimerResolutionStatus(0, 0, 0, _enabledByApp);
            }
            return new TimerResolutionStatus(finest, coarsest, current, _enabledByApp);
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning("Timer resolution query unavailable: {Error}", ex.Message);
            return new TimerResolutionStatus(0, 0, 0, _enabledByApp);
        }
    }

    /// <summary>
    /// Request the finest achievable timer resolution (clamped to the device's
    /// reported minimum). Returns the status after re-querying the effective value.
    /// </summary>
    public TimerResolutionStatus Enable()
    {
        var status = Query();
        if (status.FinestHundredNs == 0) return status; // query failed; nothing to set

        // Never request finer than the hardware allows.
        uint target = Math.Max(status.FinestHundredNs, HalfMilliInHundredNs);
        if (target > status.FinestHundredNs) target = status.FinestHundredNs;

        try
        {
            int set = NativeMethods.NtSetTimerResolution(target, true, out _);
            if (set != NativeMethods.StatusSuccess)
                Log.Debug("NtSetTimerResolution(enable) failed: NTSTATUS 0x{Status:X8}", set);
            else
                _enabledByApp = true;
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning("Timer resolution set unavailable: {Error}", ex.Message);
        }
        return Query();
    }

    /// <summary>
    /// Release this process's timer-resolution request, returning the timer toward the
    /// system default. Returns the status after re-querying the effective value.
    /// </summary>
    public TimerResolutionStatus Disable()
    {
        var status = Query();
        if (status.CoarsestHundredNs == 0) { _enabledByApp = false; return status; }

        try
        {
            // SetResolution=false releases this process's prior request.
            int rel = NativeMethods.NtSetTimerResolution(status.CoarsestHundredNs, false, out _);
            if (rel != NativeMethods.StatusSuccess)
                Log.Debug("NtSetTimerResolution(disable) failed: NTSTATUS 0x{Status:X8}", rel);
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning("Timer resolution release unavailable: {Error}", ex.Message);
        }
        _enabledByApp = false;
        return Query();
    }

    private static partial class NativeMethods
    {
        /// <summary>STATUS_SUCCESS.</summary>
        public const int StatusSuccess = 0;

        // NTSTATUS NtQueryTimerResolution(PULONG MaximumTime, PULONG MinimumTime, PULONG CurrentTime)
        // Param order is (finest, coarsest, current) — "Maximum" (finest) comes FIRST.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryTimerResolution(out uint finest, out uint coarsest, out uint current);

        // NTSTATUS NtSetTimerResolution(ULONG DesiredTime, BOOLEAN SetResolution, PULONG ActualTime)
        // BOOLEAN is a 1-byte type → marshal bool as U1.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtSetTimerResolution(
            uint desiredTime,
            [MarshalAs(UnmanagedType.U1)] bool setResolution,
            out uint actualTime);
    }
}
