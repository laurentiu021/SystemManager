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
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

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

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = argumentHint ?? string.Empty
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
