// SysManager · AppAlertService — monitors for new application installations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Monitors Program Files directories and registry uninstall keys for new
/// application installations. Raises an event when a new app is detected.
/// </summary>
public sealed class AppAlertService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Lock _watcherLock = new();
    private readonly ConcurrentDictionary<string, bool> _knownFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _knownRegistryApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _syncContext;
    private readonly Func<IReadOnlyList<AppInstallEntry>> _readRegistryApps;
    private readonly TimeProvider _clock;

    // The folders announced lately, each until an uninstall entry claims it. An installer creates its folder first and
    // writes its uninstall entry minutes later, and the two were announced as two installs (#2878).
    private readonly List<AppInstallEntry> _recentFolders = [];
    private readonly Lock _recentFoldersLock = new();
    private Timer? _registryTimer;
    private bool _disposed;

    /// <summary>Raised when a new application installation is detected.
    /// Marshaled to the <see cref="SynchronizationContext"/> captured at construction
    /// (typically the UI thread) so subscribers can safely update UI elements.</summary>
    public event Action<AppInstallEntry>? NewAppDetected;

    /// <summary>
    /// Raised instead of <see cref="NewAppDetected"/> when a new uninstall entry points into a folder announced within
    /// <see cref="RecentChangesService.SameInstall"/> before it: the folder's alert first, then the entry, whose name
    /// is the program's. Marshaled like <see cref="NewAppDetected"/>.
    /// </summary>
    public event Action<AppInstallEntry, AppInstallEntry>? AppIdentified;

    /// <summary>
    /// Creates a new instance, capturing the current <see cref="SynchronizationContext"/>
    /// so that events are raised on the UI thread.
    /// </summary>
    public AppAlertService() : this(GetRegistryApps) { }

    /// <summary>
    /// Reads the installed-programs list from <paramref name="readRegistryApps"/> instead of the registry, so a
    /// test decides what is installed before and after the baseline, and the time from <paramref name="clock"/>.
    /// </summary>
    internal AppAlertService(Func<IReadOnlyList<AppInstallEntry>> readRegistryApps, TimeProvider? clock = null)
    {
        _syncContext = SynchronizationContext.Current;
        _readRegistryApps = readRegistryApps;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Takes a snapshot of currently installed apps (baseline).
    /// Call this before starting monitoring.
    /// </summary>
    public void TakeBaseline()
    {
        foreach (var dir in GetMonitoredDirectories().Where(Directory.Exists))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    _knownFolders[sub] = true;
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }

        foreach (var app in _readRegistryApps())
            _knownRegistryApps[app.Name] = true;
    }

    /// <summary>
    /// Starts monitoring for new installations.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;

        lock (_watcherLock)
        {
            // Re-entrancy guard: a second Start() without an intervening Stop() would
            // orphan the first registry Timer (overwritten below, never disposed) and
            // add a duplicate set of FileSystemWatchers. Bail if already running.
            if (_registryTimer is not null || _watchers.Count > 0)
                return;

            foreach (var dir in GetMonitoredDirectories().Where(Directory.Exists))
            {
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

            // Create the timer inside the lock so it's covered by the re-entrancy
            // guard above (a concurrent Start() can't both pass the guard and each
            // create a timer).
            _registryTimer = new Timer(CheckRegistry, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        Log.Information("App alert monitoring started ({Watchers} watchers)", _watchers.Count);
    }

    /// <summary>
    /// Stops monitoring.
    /// </summary>
    public void Stop()
    {
        lock (_watcherLock)
        {
            // Dispose the timer under the same lock that creates it in Start(), so a
            // concurrent Start()/Stop() can't race on _registryTimer (Start creates it
            // inside the lock; tearing it down outside would reopen that window).
            _registryTimer?.Dispose();
            _registryTimer = null;

            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _watchers.Clear();
        }
        Log.Information("App alert monitoring stopped");
    }

    /// <summary>
    /// Gets a snapshot of currently installed apps from registry uninstall keys.
    /// </summary>
    public static IReadOnlyList<AppInstallEntry> GetRegistryApps()
    {
        List<AppInstallEntry> apps = [];
        string[] paths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var path in paths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName);
                        if (sub is null) continue;

                        var name = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        apps.Add(new AppInstallEntry
                        {
                            Name = name,
                            Publisher = sub.GetValue("Publisher") as string ?? "",
                            InstallPath = sub.GetValue("InstallLocation") as string ?? "",
                            Source = "Registry",
                            Locations = FoldersOf(sub.GetValue("UninstallString") as string,
                                                  sub.GetValue("DisplayIcon") as string),
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

    /// <summary>
    /// The folders an uninstall entry's uninstaller and icon are in. Its command is read by the rule the Uninstaller
    /// runs it with, so <c>MsiExec.exe /X{…}</c> names no folder. An icon's index (<c>,0</c>), and the quote before
    /// it, follow the file's name, so they never reach its folder.
    /// </summary>
    internal static IReadOnlyList<string> FoldersOf(string? uninstallCommand, string? displayIcon)
    {
        List<string> folders = [];
        if (!string.IsNullOrWhiteSpace(uninstallCommand))
        {
            try { AddFolderOf(UninstallerService.ParseUninstallCommand(uninstallCommand).Exe); }
            catch (InvalidOperationException) { /* a command the Uninstaller refuses to read names no folder here */ }
            catch (ArgumentException) { /* ditto */ }
        }

        if (!string.IsNullOrWhiteSpace(displayIcon)) AddFolderOf(displayIcon.Trim().Trim('"'));

        return folders;

        void AddFolderOf(string file)
        {
            if (Path.IsPathFullyQualified(file) && Path.GetDirectoryName(file) is { Length: > 0 } folder)
                folders.Add(folder);
        }
    }

    /// <summary>
    /// Whether the uninstall entry <paramref name="app"/> points into <paramref name="folder"/>: its install location,
    /// or the folder its uninstaller or its icon is in, is that folder or inside it.
    /// </summary>
    internal static bool BelongsTo(AppInstallEntry app, string folder) =>
        app.Locations.Prepend(app.InstallPath)
           .Select(l => l.Trim().Trim('"'))
           .Any(l => Path.IsPathFullyQualified(l) && SystemPaths.IsInsideSubtree(l, folder));

    private void OnDirectoryCreated(object sender, FileSystemEventArgs e) => AnnounceFolder(e.FullPath);

    /// <summary>Announces a new folder in a watched place once, and keeps it for its uninstall entry.</summary>
    internal void AnnounceFolder(string fullPath)
    {
        if (!_knownFolders.TryAdd(fullPath, true)) return;

        var entry = new AppInstallEntry
        {
            Name = Path.GetFileName(fullPath),
            InstallPath = fullPath,
            DetectedAt = _clock.GetLocalNow().DateTime,
            Source = "FileSystem"
        };

        Log.Information("New app folder detected: {Path}", fullPath);
        RaiseNewAppDetected(entry);

        // Kept once it is raised, so that the entry which claims it is raised after it and finds it on the list.
        lock (_recentFoldersLock) _recentFolders.Add(entry);
    }

    /// <summary>
    /// Checks the registry for new installations now, instead of waiting for the next 30-second pass.
    /// </summary>
    /// <remarks>
    /// Does nothing until <see cref="Start"/> has run. The check reports what is missing from the list
    /// <see cref="TakeBaseline"/> recorded when monitoring began; with no such list, every program on the
    /// PC would be announced as newly installed. A run that overlaps the timer's own pass is harmless:
    /// each new name is claimed once, so it is announced once.
    /// </remarks>
    /// <returns>How many new installations were found and announced.</returns>
    public int CheckNow()
    {
        lock (_watcherLock)
        {
            if (_registryTimer is null) return 0;
        }
        return CheckRegistryForNewApps();
    }

    private void CheckRegistry(object? state) => CheckRegistryForNewApps();

    private int CheckRegistryForNewApps()
    {
        var found = 0;
        try
        {
            var current = _readRegistryApps();
            foreach (var app in current
                         .Where(a => !_knownRegistryApps.ContainsKey(a.Name))
                         .Where(a => _knownRegistryApps.TryAdd(a.Name, true)))
            {
                app.DetectedAt = _clock.GetLocalNow().DateTime;
                if (ClaimFolder(app) is { } folder)
                {
                    Log.Information("New app {Name} is the folder {Path} announced before it", app.Name,
                                    folder.InstallPath);
                    Raise(() => AppIdentified?.Invoke(folder, app));
                }
                else
                {
                    Log.Information("New app detected in registry: {Name}", app.Name);
                    RaiseNewAppDetected(app);
                }
                found++;
            }
        }
        catch (IOException) { /* registry read failed — retry next cycle */ }
        catch (UnauthorizedAccessException) { /* registry read failed — retry next cycle */ }
        catch (System.Security.SecurityException) { /* registry read failed — retry next cycle */ }
        return found;
    }

    /// <summary>
    /// The folder announced within <see cref="RecentChangesService.SameInstall"/> before <paramref name="app"/> that it
    /// points into, taken off the list so that no other entry claims it, or null.
    /// </summary>
    private AppInstallEntry? ClaimFolder(AppInstallEntry app)
    {
        var since = app.DetectedAt - RecentChangesService.SameInstall;
        lock (_recentFoldersLock)
        {
            _recentFolders.RemoveAll(f => f.DetectedAt < since);
            var folder = _recentFolders.FirstOrDefault(f => BelongsTo(app, f.InstallPath));
            if (folder is not null) _recentFolders.Remove(folder);
            return folder;
        }
    }

    private void RaiseNewAppDetected(AppInstallEntry entry) => Raise(() => NewAppDetected?.Invoke(entry));

    /// <summary>
    /// Raises an event on the captured synchronization context (UI thread) if available, otherwise invokes it directly
    /// on the current thread.
    /// </summary>
    private void Raise(Action raise)
    {
        if (_syncContext is not null)
            _syncContext.Post(_ => raise(), null);
        else
            raise();
    }

    private static List<string> GetMonitoredDirectories()
    {
        List<string> dirs = [];

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

    /// <summary>Renders the newly-installed-app alerts as CSV with a header row.</summary>
    /// <remarks>
    /// The install path is included because it is the column that answers "is this the real thing", and the
    /// acknowledged flag because an exported list is usually being sent to someone who needs to know which
    /// entries the user has already looked at.
    /// </remarks>
    public static string ToCsv(IEnumerable<AppInstallEntry> alerts, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(culture);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, culture, "App", "Publisher", "Detected", "Source", "Install path", "Acknowledged");
        foreach (var a in alerts)
        {
            Csv.AppendRow(sb, culture,
                a.Name,
                a.Publisher,
                a.DetectedAt == default
                    ? null
                    : a.DetectedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                a.Source,
                a.InstallPath,
                a.IsAcknowledged ? "yes" : "no");
        }
        return sb.ToString();
    }
}
