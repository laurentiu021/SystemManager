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
/// A finer timer resolution (e.g. 0.5 ms instead of the ~15.6 ms default) can reduce
/// input latency in games, at the cost of higher power draw. The request is a
/// per-process contribution that Windows reverts when this process exits, so it is
/// fully reversible and needs no admin rights.
///
/// IMPORTANT lifetime caveat (Windows 10 2004+ / Windows 11): a request is honored
/// for the requesting process but is no longer a system-wide global override. Since
/// Windows 10 2004 it reaches only processes that request a timer themselves, which
/// then get the finest any process asked for, and by default Windows 11 stops honoring
/// it while this app's windows are hidden, minimized or covered, so Enable opts out of
/// that first. We still re-query the EFFECTIVE resolution after a set rather than
/// trusting the requested value.
/// </summary>
public sealed partial class TimerResolutionService : ITimerResolutionService
{
    private readonly ITimerResolutionNative _native;
    private bool _enabledByApp;

    /// <summary>Creates the service over the real ntdll and kernel32 calls.</summary>
    public TimerResolutionService() : this(new NtTimer()) { }

    /// <summary>Creates the service over <paramref name="native"/>, so a test sees what Windows is asked, in order.</summary>
    internal TimerResolutionService(ITimerResolutionNative native) => _native = native;

    /// <summary>Read the achievable range and the resolution currently in effect.</summary>
    public TimerResolutionStatus Query()
    {
        try
        {
            var (status, coarsest, finest, current) = _native.Query();
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
    /// Request the finest achievable timer resolution reported by the device.
    /// Returns the status after re-querying the effective value.
    /// </summary>
    public TimerResolutionStatus Enable()
    {
        var status = Query();
        if (status.FinestHundredNs == 0) return status; // query failed; nothing to set

        // FinestHundredNs is the smallest (finest) interval the hardware reports, so
        // it is already the finest we can legitimately request.
        uint target = status.FinestHundredNs;

        try
        {
            // Windows 11 stops honoring a timer request from a process whose windows are hidden, minimized or covered,
            // which is SysManager's state for as long as a full-screen game runs, so the request lapsed exactly while
            // the game it was made for ran (#2830). Asked first, so the request is kept from the moment it is made.
            KeepRequestWhileHidden(true);

            int set = _native.Set(target, true);
            if (set != NativeMethods.StatusSuccess)
            {
                Log.Debug("NtSetTimerResolution(enable) failed: NTSTATUS 0x{Status:X8}", set);
                // Nothing is requested, unless an earlier Enable's request still is.
                if (!_enabledByApp) KeepRequestWhileHidden(false);
            }
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
            int rel = _native.Set(status.CoarsestHundredNs, false);
            if (rel != NativeMethods.StatusSuccess)
                Log.Debug("NtSetTimerResolution(disable) failed: NTSTATUS 0x{Status:X8}", rel);

            // With nothing requested, Windows decides again how this process's timers are treated while it is hidden.
            KeepRequestWhileHidden(false);
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning("Timer resolution release unavailable: {Error}", ex.Message);
        }
        _enabledByApp = false;
        return Query();
    }

    /// <summary>Asks Windows to keep, or no longer keep, this process's timer request while it is hidden.</summary>
    /// <remarks>
    /// A Windows version without this throttling may refuse the flag, so a refusal is logged and nothing else: the
    /// request itself goes ahead either way.
    /// </remarks>
    private void KeepRequestWhileHidden(bool keep)
    {
        var error = _native.KeepRequestWhileHidden(keep);
        if (error != 0)
            Log.Debug("SetProcessInformation(ProcessPowerThrottling, keep: {Keep}) failed: Win32 error {Error}", keep, error);
    }

    /// <summary>
    /// The native calls behind the service: the timer query and request, and whether Windows keeps this process's
    /// request while its windows are hidden.
    /// </summary>
    internal interface ITimerResolutionNative
    {
        /// <summary>NtQueryTimerResolution: its NTSTATUS and the (coarsest, finest, current) intervals, in 100 ns.</summary>
        (int Status, uint Coarsest, uint Finest, uint Current) Query();

        /// <summary>NtSetTimerResolution: makes (<paramref name="setResolution"/> true) or releases the request. NTSTATUS.</summary>
        int Set(uint desiredTime, bool setResolution);

        /// <summary>
        /// SetProcessInformation with ProcessPowerThrottling: true keeps the request while the windows are hidden, false
        /// leaves it to Windows. 0, or the Win32 error.
        /// </summary>
        int KeepRequestWhileHidden(bool keep);
    }

    /// <summary>The real calls: ntdll's timer resolution and kernel32's power throttling of this process.</summary>
    private sealed class NtTimer : ITimerResolutionNative
    {
        public (int Status, uint Coarsest, uint Finest, uint Current) Query()
        {
            int status = NativeMethods.NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
            return (status, coarsest, finest, current);
        }

        public int Set(uint desiredTime, bool setResolution)
            => NativeMethods.NtSetTimerResolution(desiredTime, setResolution, out _);

        public int KeepRequestWhileHidden(bool keep)
        {
            // Keep: control the timer-resolution mechanism and switch its throttling off, which Microsoft's
            // SetProcessInformation documentation gives as "always honor timer resolution requests". Let go: control
            // no mechanism, which hands every one of them back to Windows. SysManager sets no other mechanism.
            var state = new NativeMethods.ProcessPowerThrottlingState
            {
                Version = NativeMethods.ProcessPowerThrottlingCurrentVersion,
                ControlMask = keep ? NativeMethods.ProcessPowerThrottlingIgnoreTimerResolution : 0,
                StateMask = 0,
            };
            return NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(),
                NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<NativeMethods.ProcessPowerThrottlingState>())
                ? 0
                : Marshal.GetLastPInvokeError();
        }
    }

    private static partial class NativeMethods
    {
        /// <summary>STATUS_SUCCESS.</summary>
        public const int StatusSuccess = 0;

        /// <summary>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling.</summary>
        public const int ProcessPowerThrottling = 4;

        /// <summary>PROCESS_POWER_THROTTLING_CURRENT_VERSION.</summary>
        public const uint ProcessPowerThrottlingCurrentVersion = 1;

        /// <summary>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION.</summary>
        public const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

        /// <summary>PROCESS_POWER_THROTTLING_STATE.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        // NTSTATUS NtQueryTimerResolution(PULONG MinimumResolution, PULONG MaximumResolution,
        //                                 PULONG CurrentResolution)
        //
        // The order is (coarsest, finest, current), and the names are the trap: "Minimum" means
        // minimum PRECISION, which is the LARGEST interval. Measured on real hardware, the call
        // returns 156250 (15.6 ms) first, then 5000 (0.5 ms), then the current value — so the
        // first out-param is the coarse Windows default, not the fine one. Binding these the other
        // way round made Enable request the 15.6 ms default while the UI (and Gaming Profile's
        // "Finest timer resolution (~0.5 ms)" toggle) claimed it had asked for 0.5 ms.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);

        // NTSTATUS NtSetTimerResolution(ULONG DesiredTime, BOOLEAN SetResolution, PULONG ActualTime)
        // BOOLEAN is a 1-byte type → marshal bool as U1.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtSetTimerResolution(
            uint desiredTime,
            [MarshalAs(UnmanagedType.U1)] bool setResolution,
            out uint actualTime);

        // HANDLE GetCurrentProcess(void): a pseudo-handle that needs no closing.
        [LibraryImport("kernel32.dll")]
        internal static partial nint GetCurrentProcess();

        // BOOL SetProcessInformation(HANDLE hProcess, PROCESS_INFORMATION_CLASS ProcessInformationClass,
        //                            LPVOID ProcessInformation, DWORD ProcessInformationSize)
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessInformation(
            nint process,
            int processInformationClass,
            ref ProcessPowerThrottlingState processInformation,
            uint processInformationSize);
    }
}
