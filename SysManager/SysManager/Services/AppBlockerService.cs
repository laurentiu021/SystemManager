// SysManager · AppBlockerService — block/unblock apps via IFEO registry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
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
public sealed class AppBlockerService : IAppBlockerService
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>The file name of the "Debugger" a block points at, a file that does not exist.</summary>
    internal const string BlockerFileName = "SysManager_Blocked.exe";

    // Sentinel "Debugger" path written into IFEO to block an app: it points at a file that
    // does not exist, so the launch fails. Derived from the real system directory rather than
    // hardcoding C:\Windows\System32 (Windows can be installed on another drive/directory).
    // A stored value is recognised by IsOwnBlock, which also knows the values older builds wrote.
    private static readonly string BlockerDebugger =
        Path.Combine(Environment.SystemDirectory, BlockerFileName);

    /// <summary>
    /// True when a stored Debugger value is a block SysManager wrote, by this build or an older one.
    /// </summary>
    /// <remarks>
    /// Recognised by its file name, not by the whole path. Builds before #1061 wrote the literal
    /// <c>C:\Windows\System32\SysManager_Blocked.exe</c>, which matches the path derived from the system directory
    /// only where Windows is in C:\Windows. Elsewhere such a block stayed in force but was not listed, so it had
    /// no Unblock button, and a second Block was refused as another program's debugger. No other program writes
    /// a debugger of that name.
    /// </remarks>
    internal static bool IsOwnBlock(string? debugger) =>
        !string.IsNullOrWhiteSpace(debugger)
        && string.Equals(Path.GetFileName(debugger.Trim()), BlockerFileName, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// True when an IFEO block on this executable name is one <see cref="TryBlockApp"/> refuses because
    /// blocking it removes the means of unblocking it.
    /// </summary>
    /// <remarks>
    /// Extracted so the refusal rule has ONE definition. It is needed in two places — refusing a new block,
    /// and recognising a block an OLDER build already wrote — and a second copy of the list is exactly how
    /// the two would come to disagree about which entries are dangerous.
    /// <para>Normalises the name the same way the block path does, so a bare "consent" and a padded
    /// " CONSENT.EXE " reach the same verdict as the value IFEO actually stores.</para>
    /// </remarks>
    internal static bool IsElevationOrBootCritical(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        var name = exeName.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        return BootCriticalExecutables.Contains(name);
    }

    /// <summary>
    /// True when a block that ALREADY EXISTS on this executable cannot be lifted from inside the app.
    /// </summary>
    /// <remarks>
    /// The guard in <see cref="TryBlockApp"/> only prevents a NEW block. It does nothing for a machine an
    /// older build already wrote one on — and that is the case that strands someone, because the undo is
    /// circular: <see cref="UnblockApp"/> writes to HKLM, which needs elevation, which for
    /// <c>consent.exe</c> needs the very process the block disabled. Before this, such an entry appeared in
    /// the blocked list as an ordinary row beside the deliberate ones, with an Unblock button that could
    /// never succeed and nothing saying why (#2357).
    /// <para>Wider than the static set by one case: the app's OWN executable. Blocking it is refused now,
    /// but a block written under the running name takes effect at the NEXT launch, so the app is still
    /// able to report it — and it is the one entry where saying nothing means the user simply finds the
    /// app gone tomorrow.</para>
    /// </remarks>
    public bool IsUnrecoverableBlock(string? exeName)
    {
        if (IsElevationOrBootCritical(exeName)) return true;

        if (string.IsNullOrWhiteSpace(exeName) || OwnExecutableName is not { } self) return false;

        var name = exeName.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        return name.Equals(self, StringComparison.OrdinalIgnoreCase);
    }

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
        if (CheckName(exeName).Fault is not NameFault.None)
        {
            Log.Warning("Rejected invalid exeName: {ExeName}", exeName);
            return BlockResult.InvalidName;
        }

        // Never block a boot/logon-critical process: an IFEO redirection here is
        // honoured during boot/login and would render Windows unbootable (reboot
        // loop / BSOD) with no way to launch this app to unblock it.
        if (IsElevationOrBootCritical(exeName))
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
            if (!string.IsNullOrEmpty(existingDebugger) && !IsOwnBlock(existingDebugger))
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
        if (CheckName(exeName).Fault is not NameFault.None)
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

            if (IsOwnBlock(appKey.GetValue("Debugger") as string))
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
    /// Checks if an executable is currently blocked by SysManager, or null when the registry could not be read.
    /// </summary>
    /// <remarks>
    /// A failed read used to be false, "not blocked", which is a claim about the machine rather than about the
    /// read (#2503).
    /// </remarks>
    public bool? IsBlocked(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;

        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        // Checked as Block and Unblock check it. A name longer than a registry key name can be reached OpenSubKey,
        // which throws ArgumentException for it, and the tab asks this before anything else when Block is pressed.
        if (CheckName(exeName).Fault is not NameFault.None) return false;

        try
        {
            using var ifeo = _baseKey.OpenSubKey(IfeoPath);
            if (ifeo is null) return false;

            using var appKey = ifeo.OpenSubKey(exeName);
            if (appKey is null) return false;

            return IsOwnBlock(appKey.GetValue("Debugger") as string);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warning("Could not read whether {Exe} is blocked: {Error}", exeName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets all currently blocked applications (blocked by SysManager), or null when the Image File Execution
    /// Options key could not be read.
    /// </summary>
    /// <remarks>
    /// A failed read used to be an empty list, so the tab said "No applications are currently blocked." and the
    /// warning for a blocked <c>consent.exe</c> could not appear (#2503). An absent key is still an empty list:
    /// nothing has ever been blocked.
    /// <para>One entry that cannot be opened is still skipped rather than failing the read. SysManager writes its
    /// blocks with the key's inherited permissions, so an entry locked against reading belongs to another
    /// program, and says nothing about SysManager's own blocks.</para>
    /// </remarks>
    public IReadOnlyList<BlockedApp>? GetBlockedApps()
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

                    if (IsOwnBlock(appKey.GetValue("Debugger") as string))
                    {
                        // Classified here rather than in the view model so the flag travels with the data
                        // and cannot be derived a second, differing way by whoever displays it.
                        blocked.Add(new BlockedApp
                        {
                            ExecutableName = subKeyName,
                            IsUnrecoverable = IsUnrecoverableBlock(subKeyName),
                        });
                    }
                }
                catch (IOException) { /* skip */ }
                catch (UnauthorizedAccessException) { /* skip */ }
                catch (SecurityException) { /* skip */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warning("Could not read the block list: {Error}", ex.Message);
            return null;
        }

        return blocked;
    }

    /// <summary>What keeps an executable name from being blocked.</summary>
    public enum NameFault
    {
        /// <summary>Nothing: the name can be blocked.</summary>
        None,

        /// <summary>The name has a folder in it, a <c>\</c> or a <c>/</c>.</summary>
        Folder,

        /// <summary>The name has a character Windows does not allow in a file name.</summary>
        Character,

        /// <summary>Nothing but spaces comes before ".exe".</summary>
        NoName,

        /// <summary>The name is longer than <see cref="MaxNameLength"/>.</summary>
        TooLong,
    }

    /// <summary>The longest name that can be blocked, the longest a registry key name or file name can be.</summary>
    internal const int MaxNameLength = 255;

    // The runtime's own list: \ / : * ? " < > | and the control characters.
    private static readonly SearchValues<char> NotInAFileName = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// What keeps <paramref name="exeName"/>, with ".exe" already on it, from being blocked, and the character at
    /// fault when it is one.
    /// </summary>
    /// <remarks>
    /// The name only becomes the name of a key under Image File Execution Options, so it has to be a single file
    /// name: no folder, which would put the key somewhere else, and no character Windows does not allow in a file
    /// name, because no program can be called that. Everything else a file name can hold is accepted. A pattern of
    /// ASCII letters, digits, <c>_</c>, <c>-</c>, <c>.</c> and space refused <c>notepad++.exe</c>,
    /// <c>Setup (1).exe</c> and names with letters such as ă or é, even from Browse, and only after the user had
    /// confirmed the block (#2850).
    /// <para>A folder is reported before a character, so a pasted path is called one rather than refused for its
    /// drive's colon. At most <see cref="MaxNameLength"/> characters, because <c>OpenSubKey</c> and
    /// <c>CreateSubKey</c> throw <see cref="ArgumentException"/> for a longer key name, and a pasted name crashed
    /// Block with the app's generic error box.</para>
    /// </remarks>
    internal static (NameFault Fault, char Character) CheckName(string exeName)
    {
        var folder = exeName.AsSpan().IndexOfAny('\\', '/');
        if (folder >= 0) return (NameFault.Folder, exeName[folder]);

        var invalid = exeName.AsSpan().IndexOfAny(NotInAFileName);
        if (invalid >= 0) return (NameFault.Character, exeName[invalid]);

        var name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        if (string.IsNullOrWhiteSpace(name)) return (NameFault.NoName, '\0');

        return exeName.Length > MaxNameLength ? (NameFault.TooLong, '\0') : (NameFault.None, '\0');
    }
}
