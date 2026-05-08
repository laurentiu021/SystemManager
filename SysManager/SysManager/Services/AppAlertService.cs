// SysManager · AppAlertService — monitors for new application installations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Monitors Program Files directories and registry uninstall keys for new
/// application installations. Raises an event when a new app is detected.
/// </summary>
public sealed class AppAlertService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _knownRegistryApps = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _registryTimer;
    private bool _disposed;

    /// <summary>Raised when a new application installation is detected.</summary>
    public event Action<AppInstallEntry>? NewAppDetected;

    /// <summary>
    /// Takes a snapshot of currently installed apps (baseline).
    /// Call this before starting monitoring.
    /// </summary>
    public void TakeBaseline()
    {
        foreach (var dir in GetMonitoredDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    _knownFolders[sub] = true;
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }

        foreach (var app in GetRegistryApps())
            _knownRegistryApps[app.Name] = true;
    }

    /// <summary>
    /// Starts monitoring for new installations.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;

        foreach (var dir in GetMonitoredDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                watcher.Created += OnDirectoryCreated;
                _watchers.Add(watcher);
            }
            catch (IOException ex) { Log.Debug(ex, "Failed to watch {Dir}", dir); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Access denied watching {Dir}", dir); }
        }

        _registryTimer = new Timer(CheckRegistry, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Log.Information("App alert monitoring started ({Watchers} watchers)", _watchers.Count);
    }

    /// <summary>
    /// Stops monitoring.
    /// </summary>
    public void Stop()
    {
        _registryTimer?.Dispose();
        _registryTimer = null;

        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        Log.Information("App alert monitoring stopped");
    }

    /// <summary>
    /// Gets a snapshot of currently installed apps from registry uninstall keys.
    /// </summary>
    public static IReadOnlyList<AppInstallEntry> GetRegistryApps()
    {
        var apps = new List<AppInstallEntry>();
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var path in paths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName);
                        if (sub == null) continue;

                        var name = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        apps.Add(new AppInstallEntry
                        {
                            Name = name,
                            Publisher = sub.GetValue("Publisher") as string ?? "",
                            InstallPath = sub.GetValue("InstallLocation") as string ?? "",
                            Source = "Registry"
                        });
                    }
                    catch (IOException) { /* skip inaccessible key */ }
                    catch (UnauthorizedAccessException) { /* skip protected key */ }
                    catch (System.Security.SecurityException) { /* skip protected key */ }
                }
            }
            catch (IOException) { /* registry path not available */ }
            catch (UnauthorizedAccessException) { /* registry path not available */ }
            catch (System.Security.SecurityException) { /* registry path not available */ }
        }

        return apps;
    }

    private void OnDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        if (_knownFolders.ContainsKey(e.FullPath)) return;
        _knownFolders[e.FullPath] = true;

        var entry = new AppInstallEntry
        {
            Name = Path.GetFileName(e.FullPath),
            InstallPath = e.FullPath,
            DetectedAt = DateTime.Now,
            Source = "FileSystem"
        };

        Log.Information("New app folder detected: {Path}", e.FullPath);
        NewAppDetected?.Invoke(entry);
    }

    private void CheckRegistry(object? state)
    {
        try
        {
            var current = GetRegistryApps();
            foreach (var app in current)
            {
                if (_knownRegistryApps.ContainsKey(app.Name)) continue;
                _knownRegistryApps[app.Name] = true;

                app.DetectedAt = DateTime.Now;
                Log.Information("New app detected in registry: {Name}", app.Name);
                NewAppDetected?.Invoke(app);
            }
        }
        catch (IOException) { /* registry read failed — retry next cycle */ }
        catch (UnauthorizedAccessException) { /* registry read failed — retry next cycle */ }
        catch (System.Security.SecurityException) { /* registry read failed — retry next cycle */ }
    }

    private static List<string> GetMonitoredDirectories()
    {
        var dirs = new List<string>();

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(pf)) dirs.Add(pf);
        if (!string.IsNullOrEmpty(pfx86) && pfx86 != pf) dirs.Add(pfx86);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var programs = Path.Combine(localAppData, "Programs");
            if (Directory.Exists(programs)) dirs.Add(programs);
        }

        return dirs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
