// SysManager · AdminHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Utilities for detecting current elevation and relaunching the app elevated on demand.
/// </summary>
public static partial class AdminHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// DIAGNOSTIC (issue: some tabs show the "needs admin" banner even after elevating):
    /// writes a one-line snapshot of this process's elevation state to the local log so a
    /// real divergence can be told apart from a two-instance situation. Logs the PID, the
    /// <see cref="IsElevated"/> result, the token's elevation type, and whether the identity
    /// is in the Administrators role. Local log only — no telemetry. Safe to remove once the
    /// admin-banner issue is root-caused.
    /// </summary>
    public static void LogElevationDiagnostics(string context)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var inAdminRole = principal.IsInRole(WindowsBuiltInRole.Administrator);

            using var process = Process.GetCurrentProcess();
            var pid = process.Id;

            Log.Information(
                "ELEVATION-DIAG [{Context}] pid={Pid} isElevated={IsElevated} inAdminRole={InAdminRole} " +
                "tokenElevationType={ElevationType} owner={Owner}",
                context, pid, IsElevated(), inAdminRole, GetTokenElevationType(), identity.Owner?.Value ?? "?");
        }
        catch (System.Security.SecurityException ex) { Log.Debug(ex, "Elevation diagnostics unavailable (security)"); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Elevation diagnostics unavailable (access)"); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug(ex, "Elevation diagnostics unavailable (win32)"); }
    }

    /// <summary>
    /// Returns the process token's elevation type: "Full" (running elevated), "Limited"
    /// (UAC split-token, not elevated), "Default" (UAC off or built-in admin), or "Unknown".
    /// This distinguishes a genuinely-elevated process from a non-elevated one more precisely
    /// than the admin-role check alone.
    /// </summary>
    private static string GetTokenElevationType()
    {
        const int TokenElevationType = 18; // TOKEN_INFORMATION_CLASS.TokenElevationType
        using var process = Process.GetCurrentProcess();
        if (!NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TOKEN_QUERY, out var token))
            return "Unknown";
        try
        {
            if (NativeMethods.GetTokenInformation(token, TokenElevationType, out var type, sizeof(int), out _))
            {
                return type switch
                {
                    1 => "Default",
                    2 => "Full",
                    3 => "Limited",
                    _ => "Unknown"
                };
            }
            return "Unknown";
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static partial class NativeMethods
    {
        internal const uint TOKEN_QUERY = 0x0008;

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
            int tokenInformationLength, out int returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);
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
    /// Relaunch the current process with UAC elevation and exit the current instance.
    /// Pass an optional argument hint so the new instance can jump back to the right tab.
    /// </summary>
    public static bool RelaunchAsAdmin(string? argumentHint = null)
    {
        if (System.Windows.Application.Current == null) return false;
        try
        {
            using var currentProc = Process.GetCurrentProcess();
            var exePath = Environment.ProcessPath ?? currentProc.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            // Always tag the elevated child so its single-instance guard waits for this
            // instance to release the mutex rather than bailing as a "duplicate".
            var arguments = string.IsNullOrWhiteSpace(argumentHint)
                ? RelaunchedElevatedArg
                : $"{RelaunchedElevatedArg} {argumentHint}";

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments
            };
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
