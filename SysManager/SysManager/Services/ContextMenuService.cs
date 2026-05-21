// SysManager · ContextMenuService — scan and toggle Explorer context menu entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Security;
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
public sealed class ContextMenuService
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

                        entries.Add(new ContextMenuEntry
                        {
                            Name = displayName,
                            Command = command,
                            RegistryPath = registryPath,
                            Location = location,
                            Source = source,
                            IsEnabled = isEnabled
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
    /// without deleting any data.
    /// </summary>
    public bool DisableEntry(ContextMenuEntry entry)
    {
        try
        {
            BackupRegistry(entry.RegistryPath);

            var relativePath = entry.RegistryPath.Replace(@"HKCR\", "", StringComparison.OrdinalIgnoreCase);
            using var key = Registry.ClassesRoot.OpenSubKey(relativePath, writable: true);
            if (key is null)
            {
                Log.Warning("Cannot disable context menu entry — key not found: {Path}", entry.RegistryPath);
                return false;
            }

            key.SetValue("LegacyDisable", "", RegistryValueKind.String);
            entry.IsEnabled = false;
            Log.Information("Context menu entry disabled: {Name} at {Path}", entry.Name, entry.RegistryPath);
            return true;
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
    /// value from the shell subkey.
    /// </summary>
    public bool EnableEntry(ContextMenuEntry entry)
    {
        try
        {
            BackupRegistry(entry.RegistryPath);

            var relativePath = entry.RegistryPath.Replace(@"HKCR\", "", StringComparison.OrdinalIgnoreCase);
            using var key = Registry.ClassesRoot.OpenSubKey(relativePath, writable: true);
            if (key is null)
            {
                Log.Warning("Cannot enable context menu entry — key not found: {Path}", entry.RegistryPath);
                return false;
            }

            key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            entry.IsEnabled = true;
            Log.Information("Context menu entry enabled: {Name} at {Path}", entry.Name, entry.RegistryPath);
            return true;
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
}
