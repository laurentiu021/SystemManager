// SysManager · AppBlockerService — block/unblock apps via IFEO registry
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Blocks applications from executing by setting a Debugger value in the
/// Image File Execution Options (IFEO) registry key. This causes Windows
/// to launch the debugger (a non-existent path) instead of the target app,
/// effectively preventing it from running. Fully reversible by removing the key.
/// Requires administrator privileges.
/// </summary>
public sealed class AppBlockerService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string BlockerDebugger = @"C:\Windows\System32\SysManager_Blocked.exe";

    /// <summary>
    /// Blocks an executable from running.
    /// </summary>
    public static bool BlockApp(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        try
        {
            using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath, writable: true);
            if (ifeo == null) return false;

            using var appKey = ifeo.CreateSubKey(exeName, writable: true);
            appKey.SetValue("Debugger", BlockerDebugger, RegistryValueKind.String);

            Log.Information("Blocked application: {ExeName}", exeName);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — admin required", exeName);
            return false;
        }
        catch (SecurityException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — security exception", exeName);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — IO error", exeName);
            return false;
        }
    }

    /// <summary>
    /// Unblocks an executable, allowing it to run again.
    /// </summary>
    public static bool UnblockApp(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        try
        {
            using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath, writable: true);
            if (ifeo == null) return false;

            using var appKey = ifeo.OpenSubKey(exeName, writable: true);
            if (appKey == null) return true;

            var debugger = appKey.GetValue("Debugger") as string;
            if (debugger != null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase))
            {
                appKey.DeleteValue("Debugger", throwOnMissingValue: false);

                if (appKey.ValueCount == 0 && appKey.SubKeyCount == 0)
                {
                    appKey.Close();
                    ifeo.DeleteSubKey(exeName, throwOnMissingSubKey: false);
                }
            }

            Log.Information("Unblocked application: {ExeName}", exeName);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Failed to unblock {ExeName} — admin required", exeName);
            return false;
        }
        catch (SecurityException ex)
        {
            Log.Warning(ex, "Failed to unblock {ExeName} — security exception", exeName);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to unblock {ExeName} — IO error", exeName);
            return false;
        }
    }

    /// <summary>
    /// Checks if an executable is currently blocked by SysManager.
    /// </summary>
    public static bool IsBlocked(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        try
        {
            using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath);
            if (ifeo == null) return false;

            using var appKey = ifeo.OpenSubKey(exeName);
            if (appKey == null) return false;

            var debugger = appKey.GetValue("Debugger") as string;
            return debugger != null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException) { return false; }
    }

    /// <summary>
    /// Gets all currently blocked applications (blocked by SysManager).
    /// </summary>
    public static IReadOnlyList<BlockedApp> GetBlockedApps()
    {
        var blocked = new List<BlockedApp>();

        try
        {
            using var ifeo = Registry.LocalMachine.OpenSubKey(IfeoPath);
            if (ifeo == null) return blocked;

            foreach (var subKeyName in ifeo.GetSubKeyNames())
            {
                try
                {
                    using var appKey = ifeo.OpenSubKey(subKeyName);
                    if (appKey == null) continue;

                    var debugger = appKey.GetValue("Debugger") as string;
                    if (debugger != null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase))
                    {
                        blocked.Add(new BlockedApp
                        {
                            ExecutableName = subKeyName,
                            BlockedAt = DateTime.Now
                        });
                    }
                }
                catch (IOException) { /* skip */ }
                catch (UnauthorizedAccessException) { /* skip */ }
                catch (SecurityException) { /* skip */ }
            }
        }
        catch (IOException) { /* registry not accessible */ }
        catch (UnauthorizedAccessException) { /* registry not accessible */ }
        catch (SecurityException) { /* registry not accessible */ }

        return blocked;
    }
}
