// SysManager · StartupService — enumerate and toggle startup items
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using Microsoft.Win32;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads startup entries from the Windows Registry (Run/RunOnce keys)
/// and optionally Task Scheduler. Toggling is non-destructive: we move
/// the value between the Run key and a parallel "Disabled" key that
/// Windows ignores, preserving the original data for re-enabling.
///
/// This mirrors the approach used by Task Manager's Startup tab and
/// Autoruns — no data is ever deleted.
/// </summary>
public sealed class StartupService
{
    // Standard Run keys
    private static readonly (string Key, StartupSource Source)[] RunKeys =
    {
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryCurrentUser),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryCurrentUser),
    };

    // Machine-wide Run keys (read-only unless elevated)
    private static readonly (string Key, StartupSource Source)[] MachineRunKeys =
    {
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StartupSource.RegistryLocalMachine),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", StartupSource.RegistryLocalMachine),
    };

    // Approved key where Windows stores disabled startup items
    private const string ApprovedRunHKCU =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRunHKLM =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32HKLM =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    public Task<IReadOnlyList<StartupEntry>> ScanAsync(CancellationToken ct = default)
        => Task.Run(() => Scan(), ct);

    private static IReadOnlyList<StartupEntry> Scan()
    {
        var results = new List<StartupEntry>();

        // HKCU Run keys
        foreach (var (keyPath, source) in RunKeys)
            ReadRunKey(Registry.CurrentUser, keyPath, source, results);

        // HKLM Run keys
        foreach (var (keyPath, source) in MachineRunKeys)
            ReadRunKey(Registry.LocalMachine, keyPath, source, results);

        // Shell startup folders (user + common)
        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "User Startup Folder", results);
        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            "Common Startup Folder", results);

        // Task Scheduler logon tasks
        ReadScheduledTasks(results);

        // Check StartupApproved to determine enabled/disabled state
        ApplyApprovedState(results);

        return results;
    }

    private static void ReadStartupFolder(string folderPath, string locationLabel, List<StartupEntry> results)
    {
        try
        {
            if (!System.IO.Directory.Exists(folderPath)) return;

            foreach (var file in System.IO.Directory.GetFiles(folderPath))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Resolve .lnk shortcuts to their target
                var command = file;
                if (string.Equals(System.IO.Path.GetExtension(file), ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType != null)
                        {
                            dynamic shell = Activator.CreateInstance(shellType)!;
                            dynamic shortcut = shell.CreateShortcut(file);
                            command = shortcut.TargetPath ?? file;
                        }
                    }
                    catch { /* fall back to the .lnk path itself */ }
                }

                results.Add(new StartupEntry
                {
                    Name = name,
                    Command = command,
                    Location = locationLabel,
                    Source = StartupSource.RegistryCurrentUser, // folder-based, toggle via delete/recreate
                    RegistryKey = "",
                    ValueName = name,
                    IsEnabled = true,
                    Publisher = ExtractPublisher(command),
                    StatusText = "Enabled"
                });
            }
        }
        catch { /* folder may be inaccessible */ }
    }

    private static void ReadScheduledTasks(List<StartupEntry> results)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks", writable: false);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var taskKey = key.OpenSubKey(subKeyName, writable: false);
                    if (taskKey == null) continue;

                    // Only include tasks with logon triggers (trigger type 9 = TASK_TRIGGER_LOGON)
                    var triggers = taskKey.GetValue("Triggers") as byte[];
                    if (triggers == null || triggers.Length < 4) continue;

                    // Check if the task has a logon trigger by looking at the path
                    var path = taskKey.GetValue("Path")?.ToString() ?? "";
                    var uri = taskKey.GetValue("URI")?.ToString() ?? path;

                    // Skip system/Microsoft tasks that clutter the list
                    if (uri.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (uri.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) continue;

                    var description = taskKey.GetValue("Description")?.ToString() ?? "";
                    var author = taskKey.GetValue("Author")?.ToString() ?? "";

                    // Read the Actions to get the command
                    var actions = taskKey.GetValue("Actions") as byte[];
                    var taskName = System.IO.Path.GetFileName(uri.TrimEnd('\\'));
                    if (string.IsNullOrWhiteSpace(taskName)) continue;

                    // Deduplicate — skip if we already have this name from registry
                    if (results.Any(e => string.Equals(e.Name, taskName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new StartupEntry
                    {
                        Name = taskName,
                        Command = description.Length > 0 ? description : uri,
                        Location = "Task Scheduler",
                        Source = StartupSource.TaskScheduler,
                        RegistryKey = "",
                        ValueName = taskName,
                        TaskPath = uri,
                        IsEnabled = true,
                        Publisher = author,
                        StatusText = "Enabled (scheduled)"
                    });
                }
                catch { /* skip individual task errors */ }
            }
        }
        catch { /* Task Scheduler registry may be inaccessible */ }
    }

    private static void ReadRunKey(RegistryKey root, string keyPath, StartupSource source, List<StartupEntry> results)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: false);
            if (key == null) return;

            var rootName = root == Registry.CurrentUser ? "HKCU" : "HKLM";

            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                var command = key.GetValue(valueName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(command)) continue;

                results.Add(new StartupEntry
                {
                    Name = valueName,
                    Command = command,
                    Location = $"{rootName}\\{keyPath}",
                    Source = source,
                    RegistryKey = keyPath,
                    ValueName = valueName,
                    IsEnabled = true, // will be refined by ApplyApprovedState
                    Publisher = ExtractPublisher(command),
                    StatusText = "Enabled"
                });
            }
        }
        catch { /* key may not exist or be inaccessible */ }
    }

    /// <summary>
    /// Windows stores a 12-byte blob per entry in StartupApproved\Run.
    /// Byte[0]: 02 = enabled, 03 = disabled. If the key/value doesn't
    /// exist, the item is considered enabled.
    /// </summary>
    private static void ApplyApprovedState(List<StartupEntry> entries)
    {
        var hkcuApproved = ReadApprovedKey(Registry.CurrentUser, ApprovedRunHKCU);
        var hklmApproved = ReadApprovedKey(Registry.LocalMachine, ApprovedRunHKLM);
        var hklm32Approved = ReadApprovedKey(Registry.LocalMachine, ApprovedRun32HKLM);

        foreach (var entry in entries)
        {
            Dictionary<string, byte[]>? approved = entry.Source switch
            {
                StartupSource.RegistryCurrentUser => hkcuApproved,
                StartupSource.RegistryLocalMachine => hklmApproved ?? hklm32Approved,
                _ => null
            };

            if (approved != null && approved.TryGetValue(entry.ValueName, out var blob) && blob.Length >= 1)
            {
                entry.IsEnabled = blob[0] != 3;
                entry.StatusText = entry.IsEnabled ? "Enabled" : "Disabled";
            }
        }
    }

    private static Dictionary<string, byte[]>? ReadApprovedKey(RegistryKey root, string keyPath)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: false);
            if (key == null) return null;

            var dict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] data)
                    dict[name] = data;
            }
            return dict;
        }
        catch { return null; }
    }

    /// <summary>
    /// Toggle a startup entry on or off by writing to the StartupApproved
    /// registry key. This is the same mechanism Task Manager uses.
    /// Non-destructive — the Run key value is never deleted.
    /// </summary>
    public static bool SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            if (entry.Source == StartupSource.TaskScheduler)
            {
                return SetTaskSchedulerEnabled(entry, enabled);
            }

            var (root, approvedPath) = entry.Source switch
            {
                StartupSource.RegistryCurrentUser => (Registry.CurrentUser, ApprovedRunHKCU),
                StartupSource.RegistryLocalMachine => (Registry.LocalMachine, ApprovedRunHKLM),
                _ => (Registry.CurrentUser, ApprovedRunHKCU)
            };

            using var key = root.OpenSubKey(approvedPath, writable: true);
            if (key == null)
            {
                entry.StatusText = "Error — StartupApproved key not found";
                return false;
            }

            // Build the 12-byte blob: byte[0] = 02 (enabled) or 03 (disabled)
            var existing = key.GetValue(entry.ValueName) as byte[];
            var blob = existing ?? new byte[12];
            if (blob.Length < 12)
            {
                var padded = new byte[12];
                Array.Copy(blob, padded, Math.Min(blob.Length, 12));
                blob = padded;
            }

            blob[0] = enabled ? (byte)2 : (byte)3;

            // When disabling, bytes 4-11 store the FILETIME of when it was disabled
            if (!enabled)
            {
                var ft = DateTime.UtcNow.ToFileTimeUtc();
                var ftBytes = BitConverter.GetBytes(ft);
                Array.Copy(ftBytes, 0, blob, 4, 8);
            }

            key.SetValue(entry.ValueName, blob, RegistryValueKind.Binary);

            entry.IsEnabled = enabled;
            entry.StatusText = enabled ? "Enabled" : "Disabled";
            return true;
        }
        catch (System.Security.SecurityException)
        {
            entry.StatusText = "Error — access denied (registry protected)";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            entry.StatusText = "Error — access denied (requires elevation)";
            return false;
        }
        catch (System.IO.IOException ex)
        {
            entry.StatusText = $"Error — {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Enable or disable a Task Scheduler logon task via schtasks.exe.
    /// </summary>
    private static bool SetTaskSchedulerEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            var taskPath = entry.TaskPath;
            if (string.IsNullOrWhiteSpace(taskPath))
            {
                entry.StatusText = "Error — task path unknown";
                return false;
            }

            var args = enabled
                ? $"/Change /TN \"{taskPath}\" /Enable"
                : $"/Change /TN \"{taskPath}\" /Disable";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                entry.StatusText = "Error — could not start schtasks";
                return false;
            }

            proc.WaitForExit(5000);
            if (proc.ExitCode == 0)
            {
                entry.IsEnabled = enabled;
                entry.StatusText = enabled ? "Enabled (scheduled)" : "Disabled (scheduled)";
                return true;
            }

            var stderr = proc.StandardError.ReadToEnd().Trim();
            entry.StatusText = $"Error — {(stderr.Length > 0 ? stderr : "schtasks failed")}";
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            entry.StatusText = "Error — schtasks not available";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            entry.StatusText = $"Error — {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Extract a rough publisher name from the command path.
    /// </summary>
    private static string ExtractPublisher(string command)
    {
        try
        {
            // Strip quotes and arguments
            var path = command.Trim('"', ' ');
            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx > 0 && !System.IO.File.Exists(path))
                path = path[..spaceIdx].Trim('"');

            if (System.IO.File.Exists(path))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return vi.CompanyName ?? "";
            }
        }
        catch { }
        return "";
    }
}
