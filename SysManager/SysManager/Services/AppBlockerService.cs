// SysManager · AppBlockerService — block/unblock apps via IFEO registry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
///
/// <para>The registry root is injectable (defaulting to
/// <see cref="Registry.LocalMachine"/>) so the IFEO writes can be unit-tested
/// against a redirected hive (e.g. a key under HKCU) without admin or touching
/// the machine's real configuration.</para>
/// </summary>
public sealed partial class AppBlockerService : IAppBlockerService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string BlockerDebugger = @"C:\Windows\System32\SysManager_Blocked.exe";

    /// <summary>
    /// Boot- and logon-critical executables that must never be blocked. An IFEO
    /// Debugger redirection on these is applied by the kernel/session manager during
    /// boot and login; blocking one (e.g. winlogon.exe or lsass.exe) causes a fatal
    /// boot/login failure that this app can no longer launch to undo. Mirrors the
    /// system-process set in <see cref="IconExtractorService"/>, restricted to the
    /// processes whose absence breaks startup.
    /// </summary>
    private static readonly HashSet<string> BootCriticalExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "winlogon.exe", "wininit.exe", "csrss.exe", "smss.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "logonui.exe",
        "explorer.exe", "svchost.exe", "ctfmon.exe", "userinit.exe", "spoolsv.exe"
    };

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// Creates the service over a registry root. Defaults to
    /// <see cref="Registry.LocalMachine"/> (the real IFEO hive); tests pass a
    /// redirected root (e.g. an HKCU subkey) to avoid admin and machine writes.
    /// </summary>
    public AppBlockerService(RegistryKey? baseKey = null)
        => _baseKey = baseKey ?? Registry.LocalMachine;

    /// <summary>
    /// Blocks an executable from running.
    /// </summary>
    public bool BlockApp(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        // SEC-004: reject path separators and invalid chars to prevent registry path injection
        if (!ExeNamePattern().IsMatch(exeName))
        {
            Log.Warning("Rejected invalid exeName: {ExeName}", exeName);
            return false;
        }

        // Never block a boot/logon-critical process: an IFEO redirection here is
        // honoured during boot/login and would render Windows unbootable (reboot
        // loop / BSOD) with no way to launch this app to unblock it.
        if (BootCriticalExecutables.Contains(exeName))
        {
            Log.Warning("Refusing to block boot-critical executable: {ExeName}", exeName);
            return false;
        }

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath, writable: true);
            if (ifeo is null) return false;

            using var appKey = ifeo.CreateSubKey(exeName, writable: true);

            // Never clobber a pre-existing Debugger value we did not set: it could
            // belong to a legitimately-debugged app, and overwriting it would both
            // break that setup and be unrecoverable (Unblock only removes OUR value).
            var existingDebugger = appKey.GetValue("Debugger") as string;
            if (!string.IsNullOrEmpty(existingDebugger) &&
                !existingDebugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Refusing to block {ExeName}: an external Debugger value is already set ({Debugger})",
                    exeName, existingDebugger);
                return false;
            }

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
    public bool UnblockApp(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        // SEC-004: apply same validation as BlockApp to prevent registry path injection
        if (!ExeNamePattern().IsMatch(exeName))
        {
            Log.Warning("Rejected invalid exeName for unblock: {ExeName}", exeName);
            return false;
        }

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath, writable: true);
            if (ifeo is null) return false;

            using var appKey = ifeo.OpenSubKey(exeName, writable: true);
            if (appKey is null) return true;

            var debugger = appKey.GetValue("Debugger") as string;
            if (debugger is not null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase))
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
    public bool IsBlocked(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath);
            if (ifeo is null) return false;

            using var appKey = ifeo.OpenSubKey(exeName);
            if (appKey is null) return false;

            var debugger = appKey.GetValue("Debugger") as string;
            return debugger is not null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException) { return false; }
    }

    /// <summary>
    /// Gets all currently blocked applications (blocked by SysManager).
    /// </summary>
    public IReadOnlyList<BlockedApp> GetBlockedApps()
    {
        List<BlockedApp> blocked = [];

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath);
            if (ifeo is null) return blocked;

            foreach (var subKeyName in ifeo.GetSubKeyNames())
            {
                try
                {
                    using var appKey = ifeo.OpenSubKey(subKeyName);
                    if (appKey is null) continue;

                    var debugger = appKey.GetValue("Debugger") as string;
                    if (debugger is not null && debugger.Equals(BlockerDebugger, StringComparison.OrdinalIgnoreCase))
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

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_\-. ]+\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExeNamePattern();
}
