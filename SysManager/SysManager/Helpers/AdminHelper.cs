// SysManager · AdminHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Security.Principal;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Utilities for detecting current elevation and relaunching the app elevated on demand.
/// </summary>
public static class AdminHelper
{
    /// <summary>
    /// How elevation is decided. Production never replaces it; a test does, so the not-elevated branch can
    /// be asserted on any host.
    /// </summary>
    /// <remarks>
    /// A seam here rather than 65 constructor parameters: <c>IsElevated()</c> has that many call sites, and
    /// they are not all in view-models — <c>ServiceRegistration</c>, <c>EtwBandwidthSource</c> and
    /// <c>TemperatureService</c> ask too. Threading a provider through all of them would be a large diff for
    /// a value that is fixed for the life of the process.
    /// <para>The reason it is needed: sixteen test cases opened with <c>if (IsElevated) return;</c> so they
    /// would not report a false failure on an elevated host — and the CI runner IS elevated, which
    /// <c>EtwBandwidthSourceTests</c> states in a comment. The result was that the elevation gate on SFC,
    /// DISM, Windows Update and nine privileged tabs asserted nothing anywhere: not on CI, and not on a
    /// workstation that cannot run the suite.</para>
    /// <para>Process-wide mutable state, so a test that replaces it belongs in the
    /// <c>ProcessWideStatics</c> collection and must restore it — <see cref="ForceElevation"/> does both.
    /// Same shape as <c>DialogService.Instance</c>, which tests already swap this way.</para>
    /// </remarks>
    internal static Func<bool> ElevationProbe { get; set; } = QueryElevation;

    public static bool IsElevated() => ElevationProbe();

    /// <summary>
    /// Pins <see cref="IsElevated"/> to <paramref name="elevated"/> until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Restores the previous probe rather than the default one, so nesting cannot leave a stale override
    /// behind — and restoring is what makes the override safe to use beside tests that read real elevation.
    /// </remarks>
    internal static IDisposable ForceElevation(bool elevated) => new ElevationScope(elevated);

    private sealed class ElevationScope : IDisposable
    {
        private readonly Func<bool> _previous;

        internal ElevationScope(bool elevated)
        {
            _previous = ElevationProbe;
            ElevationProbe = () => elevated;
        }

        public void Dispose() => ElevationProbe = _previous;
    }

    private static bool QueryElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Command-line sentinel passed to the elevated instance started by
    /// <see cref="RelaunchAsAdmin"/>. The single-instance guard in App.OnStartup recognizes
    /// it and WAITS for the outgoing instance's mutex to be released instead of treating the
    /// elevated copy as a duplicate and exiting — otherwise the elevated instance loses the
    /// single-instance race against the still-closing original and the user is left on the
    /// non-elevated window (the "tabs still ask for admin after elevating" bug).
    /// </summary>
    public const string RelaunchedElevatedArg = "--relaunched-elevated";

    /// <summary>
    /// Start an elevated copy of the current process. Returns true when the elevated copy was
    /// started, false when it could not be (no <c>Application.Current</c>, unknown process path,
    /// or the user dismissed the UAC prompt).
    /// <para><b>On true the CALLER must shut this instance down</b> —
    /// <c>Application.Current?.Shutdown()</c> — because this method deliberately does not. The
    /// elevated copy waits on the single-instance mutex (see
    /// <see cref="RelaunchedElevatedArg"/>), so an instance that stays alive leaves the user on
    /// the non-elevated window: exactly the failure the sentinel exists to prevent. All current
    /// callers do this; the earlier summary claimed the method exited the instance itself, which
    /// it never did.</para>
    /// </summary>
    /// <param name="argumentHint">
    /// Optional extra argument for the elevated copy, so it can return to the right tab. Passed
    /// through <see cref="ProcessStartInfo.ArgumentList"/>, which applies Windows quoting — a hint
    /// containing a space would otherwise split into several arguments, or add switches to a
    /// process about to run elevated. No caller passes one today.
    /// </param>
    public static bool RelaunchAsAdmin(string? argumentHint = null)
    {
        if (System.Windows.Application.Current == null) return false;
        try
        {
            using var currentProc = Process.GetCurrentProcess();
            var exePath = Environment.ProcessPath ?? currentProc.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            // Always tag the elevated child so its single-instance guard waits for this
            // instance to release the mutex rather than bailing as a "duplicate".
            // ArgumentList, not a concatenated Arguments string: it applies Windows quoting, so a
            // hint containing a space cannot split into extra arguments to a process about to run
            // elevated. No caller passes a hint today — this keeps the first one that does safe.
            psi.ArgumentList.Add(RelaunchedElevatedArg);
            if (!string.IsNullOrWhiteSpace(argumentHint))
                psi.ArgumentList.Add(argumentHint);
            // Dispose the returned Process handle — we don't track the elevated instance.
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // Process path unavailable or app shutting down.
            Log.Debug(ex, "RelaunchAsAdmin: process path unavailable");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED (user declined UAC); other codes = real Win32 error.
            if (ex.NativeErrorCode == 1223)
                Log.Information("RelaunchAsAdmin: user declined UAC prompt");
            else
                Log.Warning(ex, "RelaunchAsAdmin: Win32 error {Code}", ex.NativeErrorCode);
            return false;
        }
    }
}
