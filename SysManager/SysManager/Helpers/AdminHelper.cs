// SysManager · AdminHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Utilities for detecting current elevation and relaunching the app elevated on demand.
/// </summary>
public static partial class AdminHelper
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
    /// How the signed-in account is found. Same seam shape as <see cref="ElevationProbe"/>, for the same
    /// reason: a test needs to assert both the matching-account and the different-account branch without
    /// depending on who is actually signed in to the host running the suite.
    /// </summary>
    internal static Func<string?> InteractiveSessionSidProbe { get; set; } = QueryInteractiveSessionSid;

    /// <summary>
    /// The SID of the account signed in to the active console session, or null when it cannot be
    /// determined (no interactive session, or the lookup failed).
    /// </summary>
    public static string? InteractiveSessionSid() => InteractiveSessionSidProbe();

    /// <summary>
    /// True when SysManager is running elevated as an account DIFFERENT from the one signed in to the
    /// active console session.
    /// </summary>
    /// <remarks>
    /// The family-PC case this exists for: a standard account elevates "Run as administrator" with a
    /// parent's administrator password, and the elevated copy then runs AS that administrator — its own
    /// token, its own <c>HKEY_CURRENT_USER</c>, its own <c>%USERNAME%</c>. Nothing before this compared the
    /// two, so a per-user switch the signed-in person applied landed in the administrator's profile instead
    /// of theirs and the tab still reported it as applied (#2826).
    /// </remarks>
    public static bool IsElevatedAsAnotherUser()
    {
        if (!IsElevated()) return false;

        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User?.Value;
        if (string.IsNullOrEmpty(currentSid)) return false;

        var interactiveSid = InteractiveSessionSid();
        return !string.IsNullOrEmpty(interactiveSid)
            && !string.Equals(currentSid, interactiveSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The registry root a per-user service should read and write: the signed-in account's own
    /// <c>HKEY_CURRENT_USER</c>-equivalent hive when elevated as a different account, or
    /// <see cref="Registry.CurrentUser"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <c>HKEY_CURRENT_USER</c> is itself only ever a per-process VIEW onto
    /// <c>HKEY_USERS\&lt;the running process's own SID&gt;</c>; the signed-in account's hive sits at the
    /// same place under its own SID for as long as that account is signed in, loaded by Windows, needing
    /// no manual load. Falls back to <see cref="Registry.CurrentUser"/> — logged, not silent — on the rare
    /// failure to resolve or open that hive, rather than leaving a per-user tab unable to do anything at
    /// all; the common case the bug this exists for was filed over is the account resolving correctly
    /// (#2826).
    /// </remarks>
    public static RegistryKey PerUserRegistryRoot()
    {
        if (!IsElevatedAsAnotherUser()) return Registry.CurrentUser;

        var sid = InteractiveSessionSid();
        if (string.IsNullOrEmpty(sid))
        {
            Log.Warning("PerUserRegistryRoot: elevated as another account, but the signed-in account's SID could not be resolved; using the administrator's own hive");
            return Registry.CurrentUser;
        }

        try
        {
            if (Registry.Users.OpenSubKey(sid, writable: true) is { } hive) return hive;
            Log.Warning("PerUserRegistryRoot: elevated as another account, but {Sid}'s hive could not be opened; using the administrator's own hive", sid);
            return Registry.CurrentUser;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "PerUserRegistryRoot: denied opening {Sid}'s hive; using the administrator's own hive", sid);
            return Registry.CurrentUser;
        }
    }

    /// <summary>
    /// Resolves the active console session's user name via <c>WTSQuerySessionInformationW</c> and
    /// translates it to a SID. Fails to null rather than throwing: a session with no interactive user (a
    /// service session, or nobody signed in at the lock screen's own session) is not an error, and nor is a
    /// name <see cref="NTAccount"/> cannot translate.
    /// </summary>
    private static string? QueryInteractiveSessionSid()
    {
        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return null; // no active console session

        var userName = QuerySessionString(sessionId, NativeMethods.WtsUserName);
        if (string.IsNullOrEmpty(userName)) return null;
        var domainName = QuerySessionString(sessionId, NativeMethods.WtsDomainName);

        try
        {
            var account = string.IsNullOrEmpty(domainName) ? userName : $@"{domainName}\{userName}";
            return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException ex)
        {
            Log.Debug(ex, "InteractiveSessionSid: could not translate the session account to a SID");
            return null;
        }
    }

    private static string? QuerySessionString(uint sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { NativeMethods.WTSFreeMemory(buffer); }
    }

    private static partial class NativeMethods
    {
        internal const int WtsUserName = 5;
        internal const int WtsDomainName = 7;

        [LibraryImport("kernel32.dll")]
        internal static partial uint WTSGetActiveConsoleSessionId();

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WTSQuerySessionInformation(
            IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr buffer, out uint bytesReturned);

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
        internal static partial void WTSFreeMemory(IntPtr memory);
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
    /// started, false when it was not: no <c>Application.Current</c>, an unknown process path, the
    /// user dismissed the UAC prompt, or the user chose to keep SysManager open because something
    /// was still running (<see cref="QuitGuard"/>, #2499). That question comes BEFORE the elevated
    /// copy starts, because once it has started it is waiting for this instance to close.
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
        if (!QuitGuard.ConfirmStoppingActiveWork("Run as administrator", "Restart as administrator anyway?"))
            return false;
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
