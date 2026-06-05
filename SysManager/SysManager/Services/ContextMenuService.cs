// SysManager · ContextMenuService — scan and toggle Explorer context menu entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
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
public sealed partial class ContextMenuService
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
                using var shellKey = Registry.ClassesRoot.OpenSubKey(subKey, writable: false);
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

        return entries;
    }

    /// <summary>
    /// Disables a context menu entry by adding the <c>LegacyDisable</c>
    /// empty string value to the shell subkey. Windows hides the entry
    /// without deleting any data. Falls back to HKCU override if HKCR
    /// is not writable (system-owned entries protected by TrustedInstaller).
    /// </summary>
    public bool DisableEntry(ContextMenuEntry entry)
    {
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
        using var hkcuKey = Registry.CurrentUser.CreateSubKey(hkcuPath);
        if (hkcuKey is null) return false;

        if (set)
            hkcuKey.SetValue("LegacyDisable", "", RegistryValueKind.String);
        else
            hkcuKey.DeleteValue("LegacyDisable", throwOnMissingValue: false);

        Log.Debug("Used HKCU override for {Path}", relativePath);
        return true;
    }

    /// <summary>
    /// Exports the registry key to a .reg file before modification.
    /// Uses <c>reg export</c> which is available on all Windows versions.
    /// </summary>
    public static void BackupRegistry(string registryPath)
    {
        try
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysManager", "Backups", "ContextMenu");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = string.Join("_", registryPath.Split(Path.GetInvalidFileNameChars()));
            var backupFile = Path.Combine(backupDir, $"{safeName}_{timestamp}.reg");

            // Convert HKCR path to full form for reg.exe
            var fullPath = registryPath.Replace("HKCR", "HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
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
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("explorer"))
            {
                proc.Kill();
                proc.WaitForExit(3000);
                proc.Dispose();
            }

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            Log.Information("Explorer restarted to apply context menu changes");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning("Failed to restart Explorer: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Human-readable explanations for common context menu entries.
    /// Keyed by raw registry name (case-insensitive).
    /// </summary>
    private static readonly Dictionary<string, string> KnownExplanations = new(StringComparer.OrdinalIgnoreCase)
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
    };

    /// <summary>
    /// Explanations keyed by executable name (for entries resolved via command path).
    /// </summary>
    private static readonly Dictionary<string, string> KnownExeExplanations = new(StringComparer.OrdinalIgnoreCase)
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
    };

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
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
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
    };

    // Known DLL resource strings mapped to friendly names
    private static readonly Dictionary<string, string> KnownResourceStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@shell32.dll,-8506"] = "Open Command Prompt",
        ["@shell32.dll,-8508"] = "Open PowerShell",
        ["@shell32.dll,-8517"] = "Open Command Prompt as Administrator",
        ["@shell32.dll,-8518"] = "Open PowerShell as Administrator",
        ["@shell32.dll,-31328"] = "Share",
        ["@shell32.dll,-37400"] = "Pin to Quick Access",
    };

    // Known executable names mapped to friendly application names
    private static readonly Dictionary<string, string> KnownExeNames = new(StringComparer.OrdinalIgnoreCase)
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
    };

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
    /// Splits a CamelCase or PascalCase string into separate words.
    /// </summary>
    private static string SplitCamelCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // Remove leading dots, @ signs
        input = input.TrimStart('.', '@');

        // Replace underscores and hyphens with spaces
        input = input.Replace('_', ' ').Replace('-', ' ');

        var result = CamelCaseSplitPattern().Replace(input, " $1");
        result = ConsecutiveUpperPattern().Replace(result, " $1");
        result = MultiSpacePattern().Replace(result, " ").Trim();

        // Capitalize first letter
        if (result.Length > 0)
            result = char.ToUpper(result[0]) + result[1..];

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

            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(vi.ProductName))
                    return vi.ProductName;
                if (!string.IsNullOrWhiteSpace(vi.CompanyName))
                    return vi.CompanyName;
            }

            // Fall back to filename without extension
            return Path.GetFileNameWithoutExtension(path);
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
