// SysManager · AdminHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics;
using System.Security.Principal;

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
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = argumentHint ?? string.Empty
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // User declined UAC or another error
            return false;
        }
    }
}
