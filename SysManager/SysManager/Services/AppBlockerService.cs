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

    // Sentinel "Debugger" path written into IFEO to block an app: it points at a file that
    // does not exist, so the launch fails. Derived from the real system directory rather than
    // hardcoding C:\Windows\System32 (Windows can be installed on another drive/directory).
    // Compared case-insensitively on read, so blocks written with the old literal still match.
    private static readonly string BlockerDebugger =
        Path.Combine(Environment.SystemDirectory, "SysManager_Blocked.exe");

    /// <summary>
    /// Executables that must never be blocked, because the block cannot be undone.
    /// </summary>
    /// <remarks>
    /// Two distinct hazards, both ending in the same place — a machine the user cannot repair from inside
    /// this app.
    /// <para><b>Boot and logon critical.</b> An IFEO Debugger redirection on these is honoured by the
    /// kernel/session manager during boot and login, so blocking one (winlogon.exe, lsass.exe) causes a
    /// fatal boot/login failure and the app can never launch again to undo it. Mirrors the system-process
    /// set in <see cref="IconExtractorService"/>, restricted to the processes whose absence breaks startup.</para>
    /// <para><b>Elevation critical.</b> <c>consent.exe</c> is the UAC consent UI that the AppInfo service
    /// launches for every elevation request. Blocking it means no elevation prompt can ever appear again —
    /// and <see cref="UnblockApp"/> writes to HKLM, which requires elevation, which requires consent.exe.
    /// The undo depends on the thing it disabled, so recovery means editing the registry offline. Windows
    /// still boots and logs in fine, which is exactly why this one does not look dangerous and is worth
    /// naming separately: the machine appears healthy right up to the first time something needs admin.</para>
    /// <para>The same circular shape is what the self-block guard below exists for. A candidate belongs
    /// here if blocking it removes the means of unblocking it, whether that happens at boot or at the first
    /// elevation.</para>
    /// </remarks>
    private static readonly HashSet<string> BootCriticalExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "winlogon.exe", "wininit.exe", "csrss.exe", "smss.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "logonui.exe",
        "explorer.exe", "svchost.exe", "ctfmon.exe", "userinit.exe", "spoolsv.exe",
        "consent.exe"
    };

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// The file name of SysManager's own running executable (e.g. <c>SysManager.exe</c> in dev,
    /// <c>SysManager-vX.Y.Z.exe</c> when released), used by <see cref="BlockApp"/> to refuse a
    /// self-block. Resolved once from <see cref="Environment.ProcessPath"/> (falling back to the
    /// main-module name). Injectable so the self-block guard is unit-testable — the xUnit host's
    /// own process name is testhost/dotnet, not SysManager, so a test passes the app's real name.
    /// </summary>
    private readonly string? _ownExecutableName;

    private string? OwnExecutableName => _ownExecutableName;

    /// <summary>
    /// Creates the service over a registry root. Defaults to
    /// <see cref="Registry.LocalMachine"/> (the real IFEO hive); tests pass a
    /// redirected root (e.g. an HKCU subkey) to avoid admin and machine writes.
    /// </summary>
    /// <param name="baseKey">IFEO registry root (defaults to HKLM).</param>
    /// <param name="ownExecutableName">Override for the app's own exe file name (defaults to the
    /// real running process's file name). Tests pass this to exercise the self-block guard.</param>
    public AppBlockerService(RegistryKey? baseKey = null, string? ownExecutableName = null)
    {
        _baseKey = baseKey ?? Registry.LocalMachine;
        _ownExecutableName = ownExecutableName ?? ResolveOwnExecutableName();
    }

    private static string? ResolveOwnExecutableName()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return proc.MainModule?.ModuleName;
        }
        // MainModule can throw for a protected/cross-bitness process; a null own-name simply
        // disables the self-guard (fail-open) rather than crashing block operations.
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Why a block attempt did not succeed. <see cref="BlockApp"/> returned a bare bool for six
    /// distinct causes — four of them deliberate safety refusals — and the UI attributed all of
    /// them to missing administrator rights. That sent a user who tried to block a boot-critical
    /// executable off to relaunch elevated, where it would be refused again for the same real
    /// reason, never stated.
    /// </summary>
    public enum BlockResult
    {
        /// <summary>The block was written.</summary>
        Success,

        /// <summary>No name was supplied.</summary>
        EmptyName,

        /// <summary>The name contains path separators or characters that are not permitted.</summary>
        InvalidName,

        /// <summary>Refused: blocking it could leave Windows unbootable.</summary>
        BootCritical,

        /// <summary>Refused: blocking SysManager itself would be unrecoverable in-app.</summary>
        OwnExecutable,

        /// <summary>Refused: another program already set a Debugger value we must not clobber.</summary>
        ExternalDebuggerPresent,

        /// <summary>Windows denied the registry write — this is the one that needs elevation.</summary>
        AccessDenied,

        /// <summary>The registry write failed for another reason.</summary>
        RegistryFailure
    }

    /// <summary>
    /// Blocks an executable from running. Prefer <see cref="TryBlockApp"/>, which reports why a
    /// block was refused; this overload is kept for callers that only need success or failure.
    /// </summary>
    public bool BlockApp(string exeName) => TryBlockApp(exeName) == BlockResult.Success;

    /// <summary>
    /// Blocks an executable from running, reporting the specific outcome so the caller can tell a
    /// safety refusal from a permissions problem.
    /// </summary>
    public BlockResult TryBlockApp(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return BlockResult.EmptyName;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        // SEC-004: reject path separators and invalid chars to prevent registry path injection
        if (!ExeNamePattern().IsMatch(exeName))
        {
            Log.Warning("Rejected invalid exeName: {ExeName}", exeName);
            return BlockResult.InvalidName;
        }

        // Never block a boot/logon-critical process: an IFEO redirection here is
        // honoured during boot/login and would render Windows unbootable (reboot
        // loop / BSOD) with no way to launch this app to unblock it.
        if (BootCriticalExecutables.Contains(exeName))
        {
            Log.Warning("Refusing to block boot-critical executable: {ExeName}", exeName);
            return BlockResult.BootCritical;
        }

        // Never block SysManager's own executable. UnblockApp requires the app to be
        // running, so an IFEO block on our own exe is unrecoverable in-app (the next
        // launch is redirected to the non-existent blocker path and fails) — the same
        // "unrecoverable IFEO block" hazard the boot-critical list guards. Reachable via
        // Browse (fills in the picked exe's real name) or by typing it; matches both the
        // dev name (SysManager.exe) and the released name (SysManager-vX.Y.Z.exe).
        if (OwnExecutableName is { } self && exeName.Equals(self, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Refusing to block SysManager's own executable: {ExeName}", exeName);
            return BlockResult.OwnExecutable;
        }

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath, writable: true);
            if (ifeo is null) return BlockResult.RegistryFailure;

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
                return BlockResult.ExternalDebuggerPresent;
            }

            appKey.SetValue("Debugger", BlockerDebugger, RegistryValueKind.String);

            Log.Information("Blocked application: {ExeName}", exeName);
            return BlockResult.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — admin required", exeName);
            return BlockResult.AccessDenied;
        }
        catch (SecurityException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — security exception", exeName);
            return BlockResult.AccessDenied;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to block {ExeName} — IO error", exeName);
            return BlockResult.RegistryFailure;
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
                        blocked.Add(new BlockedApp { ExecutableName = subKeyName });
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

    // \A…\z (absolute anchors): ^…$ would accept a trailing newline after ".exe".
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z0-9_\-. ]+\.exe\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExeNamePattern();
}
