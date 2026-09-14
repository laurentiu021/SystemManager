// SysManager · ContextMenuService — scan and toggle Explorer context menu entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads context menu shell entries from HKEY_CLASSES_ROOT and provides
/// non-destructive enable/disable via the <c>LegacyDisable</c> value.
/// Windows Explorer respects this value to hide the menu entry without
/// removing the registration — safe and fully reversible.
/// </summary>
public sealed partial class ContextMenuService : IContextMenuService
{
    // Registry locations that define context menu entries
    private static readonly (string SubKey, string Location)[] ShellLocations =
    {
        (@"*\shell",                       "Files"),
        (@"Directory\shell",               "Folders"),
        (@"Directory\Background\shell",    "Directory Background"),
        (@"DesktopBackground\shell",       "Desktop"),
    };

    /// <summary>
    /// Where COM shell extensions register, which is nowhere near where verbs do.
    /// </summary>
    /// <remarks>
    /// Six roots rather than the four above: <c>Folder</c> and <c>AllFilesystemObjects</c> carry handlers
    /// but no verbs worth listing, and they are where a lot of the real noise lives — Folder is what most
    /// archive and sync tools hook.
    /// <para>This is the half of the tab that was invisible. The four verb roots are the small tidy half;
    /// the complaint that the menu takes seconds to open is caused almost entirely by handlers, because
    /// each one is a DLL Explorer has to load and ask before it can draw the menu (#1510).</para>
    /// </remarks>
    private static readonly (string SubKey, string Location)[] ShellExLocations =
    {
        (@"*\shellex\ContextMenuHandlers",                    "Files"),
        (@"Directory\shellex\ContextMenuHandlers",            "Folders"),
        (@"Directory\Background\shellex\ContextMenuHandlers", "Directory Background"),
        (@"DesktopBackground\shellex\ContextMenuHandlers",    "Desktop"),
        (@"Folder\shellex\ContextMenuHandlers",               "Folders"),
        (@"AllFilesystemObjects\shellex\ContextMenuHandlers", "Files and folders"),
    };

    /// <summary>
    /// Stands in for <c>HKEY_CLASSES_ROOT</c> so the scan can be pointed at a disposable test hive.
    /// </summary>
    /// <remarks>
    /// The reads used <c>Registry.ClassesRoot</c> directly, which made every one of them untestable —
    /// a test could only assert against whatever the developer's machine happened to have installed.
    /// Same seam, and the same reason, as <c>AppBlockerService</c>'s injectable root.
    /// </remarks>
    private readonly RegistryKey _classesRoot;

    /// <summary>Reads from <c>HKEY_CLASSES_ROOT</c> unless handed somewhere else to read from.</summary>
    public ContextMenuService(RegistryKey? classesRoot = null) =>
        _classesRoot = classesRoot ?? Registry.ClassesRoot;

    /// <summary>
    /// Scans all known registry shell locations and returns discovered
    /// context menu entries with their enabled/disabled state.
    /// </summary>
    public List<ContextMenuEntry> ScanEntries()
    {
        List<ContextMenuEntry> entries = [];

        foreach (var (subKey, location) in ShellLocations)
        {
            try
            {
                using var shellKey = _classesRoot.OpenSubKey(subKey, writable: false);
                if (shellKey is null) continue;

                foreach (var entryName in shellKey.GetSubKeyNames())
                {
                    try
                    {
                        using var entryKey = shellKey.OpenSubKey(entryName, writable: false);
                        if (entryKey is null) continue;

                        // Read command from the "command" subkey
                        var command = "";
                        using (var cmdKey = entryKey.OpenSubKey("command", writable: false))
                        {
                            command = cmdKey?.GetValue("")?.ToString() ?? "";
                        }

                        // Read display name: prefer (Default) value, fall back to key name
                        var displayName = entryKey.GetValue("")?.ToString();
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = entryName;

                        // Check if LegacyDisable exists (entry is hidden)
                        var isEnabled = entryKey.GetValue("LegacyDisable") is null;

                        // Infer source application from command path
                        var source = ExtractSource(command);

                        var registryPath = $@"HKCR\{subKey}\{entryName}";

                        // Resolve a user-friendly display name
                        var friendlyName = GetFriendlyName(displayName, command);

                        entries.Add(new ContextMenuEntry
                        {
                            Name = friendlyName,
                            RawName = displayName,
                            Command = command,
                            RegistryPath = registryPath,
                            Location = location,
                            Source = source,
                            IsEnabled = isEnabled,
                            IsSystemEntry = IsSystemEntry(displayName),
                            Explanation = GetExplanation(entryName, command)
                        });
                    }
                    catch (SecurityException ex)
                    {
                        Log.Debug("Context menu entry inaccessible {Entry}: {Error}", entryName, ex.Message);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.Debug("Context menu entry access denied {Entry}: {Error}", entryName, ex.Message);
                    }
                    catch (IOException ex)
                    {
                        Log.Debug("Context menu entry I/O error {Entry}: {Error}", entryName, ex.Message);
                    }
                }
            }
            catch (SecurityException ex)
            {
                Log.Debug("Shell key inaccessible {Key}: {Error}", subKey, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug("Shell key access denied {Key}: {Error}", subKey, ex.Message);
            }
            catch (IOException ex)
            {
                Log.Debug("Shell key I/O error {Key}: {Error}", subKey, ex.Message);
            }
        }

        entries.AddRange(ScanShellExtensions());
        return entries;
    }

    /// <summary>
    /// Reads the COM shell extensions registered under <c>shellex\ContextMenuHandlers</c>.
    /// </summary>
    /// <remarks>
    /// A handler registers as a CLSID, so the subkey name is usually meaningless to a reader — either a
    /// GUID or a vendor's internal label. The name people would recognise lives in
    /// <c>HKCR\CLSID\{guid}</c>, and the program it belongs to is derivable from that class's
    /// <c>InprocServer32</c> DLL. Both are resolved here so a row reads "7-Zip Shell Extension" with a
    /// source, rather than a GUID.
    /// <para>De-duplicated by CLSID+location: the same handler is commonly registered under several roots
    /// (Directory and Folder both, typically), and listing it three times would make the tab look worse
    /// than the problem it is describing.</para>
    /// </remarks>
    private List<ContextMenuEntry> ScanShellExtensions()
    {
        List<ContextMenuEntry> handlers = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (subKey, location) in ShellExLocations)
        {
            try
            {
                using var root = _classesRoot.OpenSubKey(subKey, writable: false);
                if (root is null) continue;

                foreach (var handlerName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var handlerKey = root.OpenSubKey(handlerName, writable: false);
                        if (handlerKey is null) continue;

                        // The CLSID is the key's (Default) value; some registrations instead USE the CLSID
                        // as the key name, so fall back to that.
                        var clsid = handlerKey.GetValue("")?.ToString();
                        if (string.IsNullOrWhiteSpace(clsid)) clsid = handlerName;
                        clsid = clsid.Trim();

                        if (!ClsidPattern().IsMatch(clsid))
                        {
                            Log.Debug("Skipping shell extension with no usable CLSID: {Root}\\{Name}", subKey, handlerName);
                            continue;
                        }

                        if (!seen.Add(clsid + "|" + location)) continue;

                        var (friendly, dll) = ResolveClsid(clsid);
                        var displayName = !string.IsNullOrWhiteSpace(friendly) ? friendly
                            : !string.IsNullOrWhiteSpace(handlerName) && !ClsidPattern().IsMatch(handlerName) ? handlerName
                            : clsid;

                        handlers.Add(new ContextMenuEntry
                        {
                            Kind = ContextMenuEntryKind.Handler,
                            Clsid = clsid,
                            Name = displayName,
                            RawName = handlerName,
                            Command = dll,
                            RegistryPath = $@"HKCR\{subKey}\{handlerName}",
                            Location = location,
                            Source = ExtractSourceFromPath(dll),
                            // Blocked-list state is machine-wide and is not read yet, so every handler
                            // reports as active. That is honest for the common case — a handler on the
                            // Blocked list is rare — and the row's switch is disabled either way.
                            IsEnabled = true,
                            IsSystemEntry = false,
                            Explanation = HandlerExplanation(dll)
                        });
                    }
                    catch (SecurityException ex) { Log.Debug("Shell extension inaccessible {Name}: {Error}", handlerName, ex.Message); }
                    catch (UnauthorizedAccessException ex) { Log.Debug("Shell extension access denied {Name}: {Error}", handlerName, ex.Message); }
                    catch (IOException ex) { Log.Debug("Shell extension I/O error {Name}: {Error}", handlerName, ex.Message); }
                }
            }
            catch (SecurityException ex) { Log.Debug("shellex key inaccessible {Key}: {Error}", subKey, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("shellex key access denied {Key}: {Error}", subKey, ex.Message); }
            catch (IOException ex) { Log.Debug("shellex key I/O error {Key}: {Error}", subKey, ex.Message); }
        }

        return handlers;
    }

    /// <summary>
    /// Turns a CLSID into the name a person would recognise and the DLL that implements it.
    /// </summary>
    /// <remarks>
    /// Returns empty strings rather than throwing when the class is not registered, which happens for
    /// real: an uninstaller that removes its DLL and its CLSID registration but leaves the
    /// <c>ContextMenuHandlers</c> entry behind is exactly the kind of leftover this tab should show.
    /// </remarks>
    internal (string Friendly, string Dll) ResolveClsid(string clsid)
    {
        try
        {
            using var clsidKey = _classesRoot.OpenSubKey($@"CLSID\{clsid}", writable: false);
            if (clsidKey is null) return ("", "");

            var friendly = clsidKey.GetValue("")?.ToString() ?? "";

            var dll = "";
            foreach (var server in (string[])["InprocServer32", "LocalServer32"])
            {
                using var serverKey = clsidKey.OpenSubKey(server, writable: false);
                var path = serverKey?.GetValue("")?.ToString();
                if (!string.IsNullOrWhiteSpace(path)) { dll = path.Trim('"'); break; }
            }

            return (friendly.Trim(), dll);
        }
        catch (SecurityException) { return ("", ""); }
        catch (UnauthorizedAccessException) { return ("", ""); }
        catch (IOException) { return ("", ""); }
    }

    /// <summary>Plain-language line for a handler row, since it has no command to describe.</summary>
    private static string HandlerExplanation(string dll) =>
        string.IsNullOrWhiteSpace(dll)
            ? "An add-on registered by a program that is no longer installed, or whose file is missing. "
              + "Explorer still looks for it every time you right-click."
            : "An add-on from another program that adds its own items to the right-click menu. Explorer "
              + "loads it and waits for it before the menu appears, so several of these make the menu slow.";

    // A registry CLSID: {8-4-4-4-12} hex, braces required. Anchored, because a handler key name is
    // attacker-influenced in the same way the verb names are (HKCR merges HKCU\Software\Classes).
    [GeneratedRegex(@"\A\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z",
                    RegexOptions.Compiled)]
    private static partial Regex ClsidPattern();

    /// <summary>
    /// Whether <c>LegacyDisable</c> is the mechanism that hides this row, refusing and logging when it
    /// is not.
    /// </summary>
    /// <remarks>
    /// Explorer honours <c>LegacyDisable</c> on a verb key only. Written onto a
    /// <c>shellex\ContextMenuHandlers</c> key it SUCCEEDS — that key is usually writable — and changes
    /// nothing. Without this refusal the tab would report handlers as disabled, leave a junk value in
    /// each one, and the menu would open exactly as slowly as before: a worse outcome than not offering
    /// the toggle at all. Hiding a handler needs the machine-wide Blocked list, a separate admin-gated
    /// change (#1510 stage 2).
    /// <para>The row's switch is already disabled through <c>CanToggle</c>, so this is not the only
    /// guard — but the preset path reaches these methods without going through a switch, so the refusal
    /// belongs where the write is.</para>
    /// </remarks>
    private static bool CanBeHiddenByLegacyDisable(ContextMenuEntry entry)
    {
        if (entry.Kind != ContextMenuEntryKind.Handler) return true;
        Log.Debug("Refused LegacyDisable on handler {Name} ({Clsid}) — Explorer ignores it there", entry.Name, entry.Clsid);
        return false;
    }

    /// <summary>
    /// Disables a context menu entry by adding the <c>LegacyDisable</c>
    /// empty string value to the shell subkey. Windows hides the entry
    /// without deleting any data. Falls back to HKCU override if HKCR
    /// is not writable (system-owned entries protected by TrustedInstaller).
    /// </summary>
    public bool DisableEntry(ContextMenuEntry entry)
    {
        if (!CanBeHiddenByLegacyDisable(entry)) return false;

        try
        {
            BackupRegistry(entry.RegistryPath);

            var relativePath = entry.RegistryPath.Replace(@"HKCR\", "", StringComparison.OrdinalIgnoreCase);

            if (TrySetLegacyDisable(relativePath, set: true))
            {
                entry.IsEnabled = false;
                Log.Information("Context menu entry disabled: {Name} at {Path}", entry.Name, entry.RegistryPath);
                return true;
            }

            Log.Warning("Cannot disable context menu entry — key not found: {Path}", entry.RegistryPath);
            return false;
        }
        catch (SecurityException ex)
        {
            Log.Warning("Cannot disable context menu entry — access denied: {Error}", ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("Cannot disable context menu entry — requires elevation: {Error}", ex.Message);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning("Cannot disable context menu entry — I/O error: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Enables a context menu entry by removing the <c>LegacyDisable</c>
    /// value from the shell subkey. Falls back to HKCU override if HKCR
    /// is not writable.
    /// </summary>
    public bool EnableEntry(ContextMenuEntry entry)
    {
        if (!CanBeHiddenByLegacyDisable(entry)) return false;

        try
        {
            BackupRegistry(entry.RegistryPath);

            var relativePath = entry.RegistryPath.Replace(@"HKCR\", "", StringComparison.OrdinalIgnoreCase);

            if (TrySetLegacyDisable(relativePath, set: false))
            {
                entry.IsEnabled = true;
                Log.Information("Context menu entry enabled: {Name} at {Path}", entry.Name, entry.RegistryPath);
                return true;
            }

            Log.Warning("Cannot enable context menu entry — key not found: {Path}", entry.RegistryPath);
            return false;
        }
        catch (SecurityException ex)
        {
            Log.Warning("Cannot enable context menu entry — access denied: {Error}", ex.Message);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("Cannot enable context menu entry — requires elevation: {Error}", ex.Message);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning("Cannot enable context menu entry — I/O error: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Attempts to set or remove LegacyDisable on the entry.
    /// First tries HKCR (works for user-installed entries). If that fails
    /// with access denied (system-owned/TrustedInstaller), falls back to
    /// creating an override in HKCU\Software\Classes which Windows merges
    /// into HKCR at runtime.
    /// </summary>
    private static bool TrySetLegacyDisable(string relativePath, bool set)
    {
        // Attempt 1: write directly to HKCR (works for user/app entries)
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(relativePath, writable: true);
            if (key is not null)
            {
                if (set)
                    key.SetValue("LegacyDisable", "", RegistryValueKind.String);
                else
                    key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
                return true;
            }
        }
        catch (SecurityException ex) { Log.Debug(ex, "Context menu HKLM write blocked (security) — falling back to HKCU"); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Context menu HKLM write denied — falling back to HKCU"); }

        // Attempt 2: HKCU override (for system-owned entries in HKLM)
        var hkcuPath = @"Software\Classes\" + relativePath;

        if (set)
        {
            using var hkcuKey = Registry.CurrentUser.CreateSubKey(hkcuPath);
            if (hkcuKey is null) return false;
            hkcuKey.SetValue("LegacyDisable", "", RegistryValueKind.String);
        }
        else
        {
            using var hkcuKey = Registry.CurrentUser.OpenSubKey(hkcuPath, writable: true);
            if (hkcuKey is null) return false;
            hkcuKey.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        }

        Log.Debug("Used HKCU override for {Path}", relativePath);
        return true;
    }

    /// <summary>
    /// True when <paramref name="registryPath"/> is safe to interpolate into a <c>reg.exe</c> command
    /// line.
    /// </summary>
    /// <remarks>
    /// SEC: the path is built from a subkey NAME enumerated out of HKEY_CLASSES_ROOT
    /// (<c>GetSubKeyNames</c>), and <c>HKCR\*\shell</c> merges <c>HKCU\Software\Classes</c>, which an
    /// unprivileged user can write. Registry key names may legally contain a double quote, so a key
    /// named <c>evil" "C:\somewhere\out.reg" /y</c> closed the intended <c>"{fullPath}"</c> argument
    /// early and appended further arguments to <c>reg.exe</c> — controlling the export DESTINATION.
    /// With <c>UseShellExecute = false</c> that is argument injection rather than command chaining, so
    /// the impact is bounded at writing a .reg export to a chosen path; but SysManager is commonly run
    /// as administrator, and then that write lands with administrator rights outside the intended
    /// backup root.
    /// <para>The sanitisation that already existed applied only to the backup FILE NAME
    /// (<c>Path.GetInvalidFileNameChars</c>); the value interpolated into the arguments was raw. The
    /// identical guard is already applied to the analogous <c>schtasks</c> call in
    /// <c>StartupService.SetTaskEnabled</c> — this is a missed instance of a pattern the project had
    /// already decided on, which is why the check is that same shape rather than a new invention.</para>
    /// <para>NUL is rejected for the reason it is in the sibling guard: it terminates the native command
    /// line, so everything after it silently disappears.</para>
    /// </remarks>
    internal static bool IsSafeRegistryPath(string registryPath) =>
        !string.IsNullOrWhiteSpace(registryPath)
        && !registryPath.Contains('"')
        && !registryPath.Contains('\0');

    /// <summary>
    /// Exports the registry key to a .reg file before modification.
    /// Uses <c>reg export</c> which is available on all Windows versions.
    /// </summary>
    public static void BackupRegistry(string registryPath)
    {
        // Refuse before anything is built: a path that cannot be passed safely cannot be backed up
        // safely either, and the backup is best-effort — skipping one is strictly better than handing
        // attacker-controlled arguments to a possibly-elevated reg.exe. Logged at Warning, unlike the
        // ordinary "reg export returned non-zero" case below, because this means the enumerated key name
        // itself is hostile.
        if (!IsSafeRegistryPath(registryPath))
        {
            Log.Warning(
                "Registry backup refused — the key name contains a character that cannot be passed "
                + "safely to reg.exe: {Path}", registryPath);
            return;
        }

        try
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysManager", "Backups", "ContextMenu");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safeName = string.Join("_", registryPath.Split(Path.GetInvalidFileNameChars()));
            var backupFile = Path.Combine(backupDir, $"{safeName}_{timestamp}.reg");

            // Convert HKCR path to full form for reg.exe
            var fullPath = registryPath.Replace("HKCR", "HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool("reg.exe"),
                Arguments = $"export \"{fullPath}\" \"{backupFile}\" /y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            if (proc is { ExitCode: 0 })
                Log.Debug("Registry backup created: {File}", backupFile);
            else
                Log.Debug("Registry backup skipped (reg export returned non-zero)");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Backup is best-effort — don't block the actual operation
            Log.Debug("Registry backup failed (non-critical): {Error}", ex.Message);
        }
    }

    /// <summary>
    /// The registry key that controls whether Windows 11 shows the classic
    /// full context menu or the modern compact menu.
    /// </summary>
    private const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

    /// <summary>
    /// Checks whether the classic (Win10-style) context menu is currently forced.
    /// </summary>
    public static bool IsClassicMenuEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ClassicMenuClsid, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enables the classic (Win10-style) full context menu by creating the
    /// InprocServer32 key with an empty default value.
    /// </summary>
    public static bool EnableClassicMenu()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ClassicMenuClsid);
            key.SetValue("", "", Microsoft.Win32.RegistryValueKind.String);
            Log.Information("Classic context menu enabled via registry");
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warning("Failed to enable classic context menu: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Disables the classic context menu, restoring Win11's modern menu.
    /// </summary>
    public static bool DisableClassicMenu()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", throwOnMissingSubKey: false);
            Log.Information("Classic context menu disabled — restored Win11 modern menu");
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warning("Failed to disable classic context menu: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Restarts Windows Explorer to apply context menu style changes.
    /// Each process is killed individually so one unkillable instance (e.g. a
    /// higher-integrity or other-session explorer) cannot abort the loop and
    /// leave the user without a shell.
    /// </summary>
    public static void RestartExplorer()
    {
        foreach (var proc in Process.GetProcessesByName("explorer"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Debug("Could not kill explorer PID {Pid}: {Error}", proc.Id, ex.Message);
            }
            finally
            {
                proc.Dispose();
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe")) { UseShellExecute = true })?.Dispose();
            Log.Information("Explorer restarted to apply context menu changes");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Failed to relaunch Explorer: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Human-readable explanations for common context menu entries.
    /// Keyed by raw registry name (case-insensitive).
    /// </summary>
    private static readonly FrozenDictionary<string, string> KnownExplanations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "Opens the file with its default associated application",
        ["edit"] = "Opens the file in the default text editor (Notepad)",
        ["print"] = "Sends the file directly to the default printer",
        ["runas"] = "Runs the executable with elevated (admin) privileges",
        ["runasuser"] = "Runs the executable as a different Windows user account",
        ["find"] = "Opens Windows Search in the selected folder",
        ["explore"] = "Opens the folder in a new File Explorer window",
        ["cmd"] = "Opens a Command Prompt (cmd.exe) in the current folder",
        ["powershell"] = "Opens a PowerShell window in the current folder",
        ["properties"] = "Shows the file/folder properties dialog (size, attributes, security)",
        ["copy"] = "Copies the selected file(s) to the clipboard",
        ["cut"] = "Cuts the selected file(s) — moves them when pasted elsewhere",
        ["paste"] = "Pastes previously copied/cut files into the current folder",
        ["delete"] = "Moves the selected file(s) to the Recycle Bin",
        ["rename"] = "Allows you to change the file or folder name",
        ["pintohomefile"] = "Adds the folder to the Quick Access section in File Explorer sidebar",
        ["PinToStartScreen"] = "Pins the application to the Windows Start menu",
        ["Windows.ModernShare"] = "Opens the Windows sharing panel to send via email, Bluetooth, or nearby devices",
        ["opennewwindow"] = "Opens the folder in a separate File Explorer window",
        ["opennewtab"] = "Opens the folder in a new File Explorer tab",
        ["EditStickers"] = "Opens the desktop stickers editor (Windows 11 feature)",
        ["removeproperties"] = "Opens a dialog to remove file metadata and personal information",
        ["Troubleshoot compatibility"] = "Runs the Program Compatibility Troubleshooter for older apps",
        ["git_bash"] = "Opens a Git Bash terminal in the current directory",
        ["git_gui"] = "Opens Git GUI for visual staging, committing and history browsing",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explanations keyed by executable name (for entries resolved via command path).
    /// </summary>
    private static readonly FrozenDictionary<string, string> KnownExeExplanations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "Opens the file or folder in Visual Studio Code editor",
        ["notepad++"] = "Opens the file in Notepad++ text editor",
        ["notepad"] = "Opens the file in Windows Notepad",
        ["vlc"] = "Opens the media file in VLC Media Player",
        ["7zFM"] = "Opens the archive in 7-Zip File Manager",
        ["WinRAR"] = "Opens the archive in WinRAR",
        ["mspaint"] = "Opens the image in Microsoft Paint",
        ["wt"] = "Launches Windows Terminal in the selected folder",
        ["pwsh"] = "Opens PowerShell 7 in the current folder",
        ["git-bash"] = "Opens a Git Bash terminal in the current directory",
        ["git-gui"] = "Opens Git GUI for visual staging and committing",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the human-readable explanation for an entry based on its raw name and command.
    /// </summary>
    public static string GetExplanation(string rawName, string command)
    {
        if (KnownExplanations.TryGetValue(rawName, out var explanation))
            return explanation;

        // Try to match by exe name from command
        if (!string.IsNullOrWhiteSpace(command))
        {
            try
            {
                var path = command.Trim('"', ' ');
                var spaceIdx = path.IndexOf(' ');
                if (spaceIdx > 0 && !System.IO.File.Exists(path))
                    path = path[..spaceIdx].Trim('"');
                var exeName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(exeName) && KnownExeExplanations.TryGetValue(exeName, out var exeExplanation))
                    return exeExplanation;
            }
            catch (ArgumentException ex) { Log.Debug(ex, "Context menu command path parse failed: {Command}", command); }
        }

        return "";
    }

    // Well-known registry entry names mapped to user-friendly display names
    private static readonly FrozenDictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = "Command Prompt",
        ["powershell"] = "PowerShell",
        ["Windows.ModernShare"] = "Share",
        ["pintohomefile"] = "Pin to Quick Access",
        ["PinToStartScreen"] = "Pin to Start",
        ["removeproperties"] = "Remove Properties",
        ["EditStickers"] = "Edit Stickers",
        ["find"] = "Search",
        ["opennewwindow"] = "Open New Window",
        ["opennewtab"] = "Open New Tab",
        ["Troubleshoot compatibility"] = "Troubleshoot Compatibility",
        ["runas"] = "Run as Administrator",
        ["runasuser"] = "Run as Different User",
        ["edit"] = "Edit",
        ["open"] = "Open",
        ["print"] = "Print",
        ["explore"] = "Explore",
        ["properties"] = "Properties",
        ["copy"] = "Copy",
        ["cut"] = "Cut",
        ["paste"] = "Paste",
        ["delete"] = "Delete",
        ["rename"] = "Rename",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Known DLL resource strings mapped to friendly names
    private static readonly FrozenDictionary<string, string> KnownResourceStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["@shell32.dll,-8506"] = "Open Command Prompt",
        ["@shell32.dll,-8508"] = "Open PowerShell",
        ["@shell32.dll,-8517"] = "Open Command Prompt as Administrator",
        ["@shell32.dll,-8518"] = "Open PowerShell as Administrator",
        ["@shell32.dll,-31328"] = "Share",
        ["@shell32.dll,-37400"] = "Pin to Quick Access",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Known executable names mapped to friendly application names
    private static readonly FrozenDictionary<string, string> KnownExeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["git-bash"] = "Git Bash",
        ["git-gui"] = "Git GUI",
        ["code"] = "Visual Studio Code",
        ["notepad++"] = "Notepad++",
        ["notepad"] = "Notepad",
        ["vlc"] = "VLC Media Player",
        ["7zFM"] = "7-Zip File Manager",
        ["WinRAR"] = "WinRAR",
        ["mspaint"] = "Paint",
        ["calc"] = "Calculator",
        ["explorer"] = "Windows Explorer",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["cmd"] = "Command Prompt",
        ["wt"] = "Windows Terminal",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a raw registry entry name into a user-friendly display name.
    /// Falls back to CamelCase splitting and cleanup for unknown entries.
    /// </summary>
    private static string GetFriendlyName(string rawName, string command)
    {
        // 1. Check exact match in known names
        if (KnownNames.TryGetValue(rawName, out var known))
            return known;

        // 2. Check if it's a resource string like @shell32.dll,-8506
        if (rawName.StartsWith('@'))
        {
            if (KnownResourceStrings.TryGetValue(rawName, out var resourceName))
                return resourceName;

            // Try to extract a meaningful name from DLL resource references
            // Format: @C:\Windows\System32\display.dll,-4
            var dllMatch = DllResourcePattern().Match(rawName);
            if (dllMatch.Success)
            {
                var dllName = dllMatch.Groups[1].Value;
                return $"{SplitCamelCase(dllName)} (System)";
            }

            // Unknown resource string — just mark as system
            return $"{rawName} (System)";
        }

        // 3. Handle dot-prefixed names like .SpotlightLearnMore
        if (rawName.StartsWith('.'))
        {
            var cleaned = rawName.TrimStart('.');
            // Insert " — " at boundaries for known patterns
            if (cleaned.StartsWith("Spotlight", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = cleaned["Spotlight".Length..];
                if (!string.IsNullOrEmpty(suffix))
                    return $"Spotlight — {SplitCamelCase(suffix)}";
                return "Spotlight";
            }
            return SplitCamelCase(cleaned);
        }

        // 4. Try to infer from command if the raw name is cryptic
        if (IsCrypticName(rawName) && !string.IsNullOrWhiteSpace(command))
        {
            var inferred = InferNameFromCommand(command);
            if (!string.IsNullOrEmpty(inferred))
                return inferred;
        }

        // 5. Split CamelCase for readable raw names
        return SplitCamelCase(rawName);
    }

    /// <summary>
    /// Determines if an entry is a system/internal entry based on its raw name.
    /// </summary>
    private static bool IsSystemEntry(string rawName)
    {
        return rawName.StartsWith('@') || rawName.StartsWith('.');
    }

    /// <summary>
    /// Determines if a name looks cryptic and not user-friendly.
    /// </summary>
    private static bool IsCrypticName(string name)
    {
        // Names with only lowercase and no spaces, very short, or contain hyphens/underscores
        if (name.Length <= 2) return true;
        if (name.Contains("__") || name.Contains("--")) return true;
        return false;
    }

    /// <summary>
    /// Infers a display name from the command executable.
    /// </summary>
    private static string InferNameFromCommand(string command)
    {
        try
        {
            // Try to extract executable name
            var path = command.Trim('"', ' ');
            var percentIdx = path.IndexOf('%');
            if (percentIdx > 0) path = path[..percentIdx].Trim();

            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx > 0 && !File.Exists(path))
                path = path[..spaceIdx].Trim('"');

            var exeName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(exeName) && KnownExeNames.TryGetValue(exeName, out var friendly))
                return friendly;
        }
        catch (ArgumentException)
        {
            // Invalid path characters — skip inference
        }

        return "";
    }

    /// <summary>
    /// Removes a Windows menu accelerator ampersand from a label. A single '&amp;' before
    /// a character is the mnemonic marker and is dropped; a literal "&amp;&amp;" collapses to one
    /// '&amp;'. Used because context-menu labels render in a plain TextBlock, which shows
    /// '&amp;' verbatim rather than treating it as an accelerator.
    /// </summary>
    internal static string StripMnemonic(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('&')) return input;
        // Single left-to-right scan: a doubled "&&" is a literal ampersand
        // (emit one), a lone "&" is the accelerator marker (dropped).
        var sb = new System.Text.StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '&')
            {
                if (i + 1 < input.Length && input[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits a CamelCase or PascalCase string into separate words.
    /// </summary>
    internal static string SplitCamelCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // Strip Windows accelerator ampersands (e.g. "&Open", "Scan with Microsoft
        // &Defender") — a plain TextBlock renders '&' literally, so a shell-verb name
        // carrying a mnemonic would otherwise show the '&' mid-word.
        input = StripMnemonic(input);

        // Remove leading dots, @ signs
        input = input.TrimStart('.', '@');

        // Replace underscores and hyphens with spaces
        input = input.Replace('_', ' ').Replace('-', ' ');

        var result = CamelCaseSplitPattern().Replace(input, " $1");
        result = ConsecutiveUpperPattern().Replace(result, " $1");
        result = MultiSpacePattern().Replace(result, " ").Trim();

        // Capitalize first letter. Invariant on purpose: these are shell-verb / app
        // identifiers, not locale-sensitive prose — char.ToUpper maps 'i'→'İ' on tr-TR
        // and would corrupt entry names for Turkish users.
        if (result.Length > 0)
            result = char.ToUpperInvariant(result[0]) + result[1..];

        return result;
    }

    /// <summary>
    /// Extracts a source application name from the command path.
    /// </summary>
    private static string ExtractSource(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        try
        {
            // Strip quotes and arguments
            var path = command.Trim('"', ' ');

            // Handle %1 and other arguments
            var percentIdx = path.IndexOf('%');
            if (percentIdx > 0) path = path[..percentIdx].Trim();

            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx > 0 && !File.Exists(path))
                path = path[..spaceIdx].Trim('"');

            return DescribeFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>
    /// Names the program a file belongs to, preferring its product then its company name, and falling
    /// back to the bare file name when the file is not there to ask.
    /// </summary>
    private static string DescribeFile(string path)
    {
        if (File.Exists(path))
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.ProductName))
                return vi.ProductName;
            if (!string.IsNullOrWhiteSpace(vi.CompanyName))
                return vi.CompanyName;
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Source for a handler, whose registration is a bare DLL path rather than a command line.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="ExtractSource"/>. That one truncates at the first space because a
    /// verb's registration is <c>"C:\…\app.exe" "%1"</c> and everything past the executable is arguments.
    /// A shell extension's <c>InprocServer32</c> value is a path and nothing else, so the same rule turns
    /// <c>C:\Program Files\Vendor\ext.dll</c> into "Program".
    /// <para>It only truncates when the file is MISSING — the split is guarded by
    /// <c>!File.Exists</c> — so an installed extension came out right either way. The rows it ruined are
    /// precisely the ones this list exists to surface: an add-on left behind by an uninstalled program,
    /// which is where a readable name matters most because there is no running program to recognise it
    /// by.</para>
    /// <para>Environment variables are expanded because system handlers register as
    /// <c>%SystemRoot%\system32\…</c>. Unexpanded, the path never matches a file, so those rows fell back
    /// to a bare file name instead of naming Windows as the source.</para>
    /// </remarks>
    private static string ExtractSourceFromPath(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath)) return "";

        try
        {
            return DescribeFile(Environment.ExpandEnvironmentVariables(dllPath.Trim('"', ' ')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"@.*?\\?([^\\,]+)\.dll", RegexOptions.IgnoreCase)]
    private static partial Regex DllResourcePattern();

    [GeneratedRegex(@"(?<=[a-z0-9])([A-Z])")]
    private static partial Regex CamelCaseSplitPattern();

    [GeneratedRegex(@"(?<=[A-Z])([A-Z])(?=[a-z])")]
    private static partial Regex ConsecutiveUpperPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpacePattern();
}
