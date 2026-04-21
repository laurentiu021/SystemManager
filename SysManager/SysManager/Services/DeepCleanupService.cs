// SysManager · DeepCleanupService — safe-by-design scanner & cleaner
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Safe deep-cleanup scanner. Scan is read-only; Clean deletes only the
/// opted-in categories. Vendor caches / launcher caches are included but
/// game files, logins and browser data are never touched.
///
/// Both Scan and Clean accept an <see cref="IProgress{T}"/> so the UI can
/// show a determinate progress bar and the current bucket being scanned.
/// </summary>
public sealed class DeepCleanupService
{
    public sealed record ScanProgress(int Current, int Total, string CategoryName);

    public Task<IReadOnlyList<CleanupCategory>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(progress, ct), ct);

    public Task<CleanupResult> CleanAsync(
        IReadOnlyList<CleanupCategory> categories,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Clean(categories, progress, ct), ct);

    // ---------- scan definitions (built once, then iterated with progress) ----------

    private sealed record Def(
        string Name,
        string Description,
        string[] Paths,
        TimeSpan? OlderThan = null,
        bool IsDestructiveHint = false);

    private static List<Def> BuildDefinitions()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData  = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var systemDrive  = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var windowsDir   = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var tempUser     = Path.GetTempPath();
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var defs = new List<Def>
        {
            new("NVIDIA installer leftovers",
                "Extracted driver packages NVIDIA drops on your drive root and in ProgramData during an install. Safe to remove once the driver is installed.",
                new[]
                {
                    Path.Combine(systemDrive, "NVIDIA"),
                    Path.Combine(programData, "NVIDIA Corporation", "Downloader"),
                    Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"),
                    Path.Combine(programData, "NVIDIA Corporation", "Installer2"),
                    Path.Combine(localAppData, "NVIDIA", "GLCache"),
                    Path.Combine(localAppData, "NVIDIA", "DXCache"),
                    Path.Combine(localAppData, "NVIDIA", "ComputeCache"),
                }),

            new("AMD installer leftovers",
                "Unpacked driver installer folder AMD creates on the root of C:\\. Confirmed safe by AMD community docs.",
                new[] { Path.Combine(systemDrive, "AMD") }),

            new("Intel driver extracts",
                "Temporary driver package extracts from Intel installers.",
                new[] { Path.Combine(systemDrive, "Intel") }),

            new("Windows Update cache",
                "Previously downloaded Windows Update packages. Windows re-downloads anything it still needs next time.",
                new[] { Path.Combine(windowsDir, "SoftwareDistribution", "Download") }),

            new("Delivery Optimization cache",
                "Peer-to-peer update cache. Regenerated on demand.",
                new[] { Path.Combine(windowsDir, "SoftwareDistribution", "DeliveryOptimization", "Cache") }),

            new("Windows Installer patch cache",
                "C:\\Windows\\Installer\\$PatchCache$ stores baseline patch files used only when uninstalling an MSI patch. Safe per Microsoft devblog.",
                new[] { Path.Combine(windowsDir, "Installer", "$PatchCache$") }),

            new("Temporary files",
                "Per-user and system TEMP folders. Anything still in use is skipped automatically.",
                new[] { tempUser, Path.Combine(windowsDir, "Temp") }),

            new("Prefetch files",
                "Windows boot/launch prefetch cache. Windows rebuilds it as apps are used.",
                new[] { Path.Combine(windowsDir, "Prefetch") }),

            new("Crash dumps & error reports",
                "Windows Error Reporting queue and user-mode crash dumps (*.dmp).",
                new[]
                {
                    Path.Combine(localAppData, "CrashDumps"),
                    Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                }),

            new("Old Windows servicing logs (> 30 days)",
                "CBS logs older than 30 days. Windows keeps rolling ones itself.",
                new[] { Path.Combine(windowsDir, "Logs", "CBS") },
                OlderThan: TimeSpan.FromDays(30)),

            new("DirectX shader cache",
                "Precompiled GPU shaders cached by Windows. Rebuilt automatically the next time games run — clearing can fix stutter.",
                new[] { Path.Combine(localAppData, "D3DSCache") }),

            new("Recycle Bin (all drives)",
                "Emptying the recycle bin on every fixed drive.",
                DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"))
                    .ToArray()),

            new("Steam — browser & depot cache",
                "Steam web browser cache, HTML cache, app cache and depot lookup cache. Doesn't touch game files, downloads or logins.",
                SteamCacheDirs(pfx86, pf, localAppData)),

            new("Steam — shader cache",
                "Per-game shader cache under steamapps\\shadercache. Rebuilt on next launch — clearing can fix stutter or shader corruption.",
                SteamShaderCacheDirs(pfx86, pf)),

            new("Epic Games Launcher — webcache & logs",
                "Epic Launcher browser webcache and log files. Doesn't affect your Epic login or installed games.",
                new[]
                {
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache_4147"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache_4430"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "Logs"),
                    Path.Combine(localAppData, "UnrealEngineLauncher", "Saved", "webcache"),
                }),

            new("Battle.net — cache",
                "Battle.net agent and Blizzard launcher cache. Doesn't touch installed games or logins.",
                new[]
                {
                    Path.Combine(programData, "Battle.net", "Agent", "data", "cache"),
                    Path.Combine(programData, "Blizzard Entertainment", "Battle.net", "Cache"),
                    Path.Combine(localAppData, "Battle.net", "Cache"),
                }),

            new("Riot Client / League of Legends — logs",
                "Riot Client and League client logs only. No game files or credentials.",
                new[]
                {
                    Path.Combine(localAppData, "Riot Games", "Riot Client", "Logs"),
                    Path.Combine(pfx86, "Riot Games", "League of Legends", "Logs"),
                    Path.Combine(pf,    "Riot Games", "League of Legends", "Logs"),
                }),

            new("GOG Galaxy — cache",
                "GOG Galaxy launcher webcache and redists installer cache.",
                new[]
                {
                    Path.Combine(localAppData, "GOG.com", "Galaxy", "webcache"),
                    Path.Combine(programData, "GOG.com", "Galaxy", "redists"),
                }),

            new("EA App / Origin — cache",
                "EA Desktop (and legacy Origin) browser cache and logs. Doesn't affect installed games or logins.",
                new[]
                {
                    Path.Combine(localAppData, "Electronic Arts", "EA Desktop", "CEF-Cache"),
                    Path.Combine(localAppData, "Electronic Arts", "EA Desktop", "Logs"),
                    Path.Combine(localAppData, "Origin", "Logs"),
                    Path.Combine(programData, "Origin", "Logs"),
                }),
        };

        // Windows.old — optional, never auto-selected
        var windowsOld = Path.Combine(systemDrive, "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            defs.Add(new Def(
                "Windows.old (previous Windows installation)",
                "Remove only if you're sure you don't want to roll back to your previous Windows version. Windows normally auto-deletes this after 10 days.",
                new[] { windowsOld },
                IsDestructiveHint: true));
        }

        return defs;
    }

    // ---------- scanning ----------

    private static IReadOnlyList<CleanupCategory> Scan(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var defs = BuildDefinitions();
        var results = new List<CleanupCategory>(defs.Count);
        var total = defs.Count;

        for (var i = 0; i < defs.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var d = defs[i];
            progress?.Report(new ScanProgress(i + 1, total, d.Name));

            var existing = d.Paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            long size = 0; var files = 0;
            var cutoff = d.OlderThan.HasValue ? DateTime.UtcNow - d.OlderThan.Value : (DateTime?)null;

            foreach (var p in existing)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var file in EnumerateFiles(p, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (cutoff.HasValue)
                        {
                            var fi = new FileInfo(file);
                            if (fi.LastWriteTimeUtc >= cutoff.Value) continue;
                            size += fi.Length;
                        }
                        else
                        {
                            size += SafeLength(file);
                        }
                        files++;
                    }
                    catch { }
                }
            }

            results.Add(new CleanupCategory
            {
                Name = d.Name,
                Description = d.Description,
                Paths = existing,
                TotalSizeBytes = size,
                FileCount = files,
                OlderThan = d.OlderThan,
                IsDestructiveHint = d.IsDestructiveHint,
                IsSelected = size > 0 && !d.IsDestructiveHint
            });
        }

        progress?.Report(new ScanProgress(total, total, "Done"));
        return results;
    }

    // ---------- launcher roots ----------

    private static string[] SteamRoots(string pfx86, string pf)
    {
        var roots = new List<string>
        {
            Path.Combine(pfx86, "Steam"),
            Path.Combine(pf, "Steam"),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var candidate = Path.Combine(drive.RootDirectory.FullName, "Steam");
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SteamCacheDirs(string pfx86, string pf, string localAppData)
    {
        var result = new List<string>();
        foreach (var root in SteamRoots(pfx86, pf))
        {
            result.Add(Path.Combine(root, "appcache"));
            result.Add(Path.Combine(root, "htmlcache"));
            result.Add(Path.Combine(root, "depotcache"));
            result.Add(Path.Combine(root, "logs"));
        }
        result.Add(Path.Combine(localAppData, "Steam", "htmlcache"));
        return result.ToArray();
    }

    private static string[] SteamShaderCacheDirs(string pfx86, string pf)
    {
        var result = new List<string>();
        foreach (var root in SteamRoots(pfx86, pf))
            result.Add(Path.Combine(root, "steamapps", "shadercache"));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var candidate = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "shadercache");
            if (Directory.Exists(candidate)) result.Add(candidate);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------- cleaning ----------

    private static CleanupResult Clean(IReadOnlyList<CleanupCategory> categories, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long freed = 0;
        var errors = new List<string>();
        var filesDeleted = 0;
        var selected = categories.Where(c => c.IsSelected).ToList();
        var total = selected.Count;

        for (var idx = 0; idx < selected.Count; idx++)
        {
            if (ct.IsCancellationRequested) break;
            var cat = selected[idx];
            progress?.Report(new ScanProgress(idx + 1, total, "Cleaning " + cat.Name));

            var cutoff = cat.OlderThan.HasValue ? DateTime.UtcNow - cat.OlderThan.Value : (DateTime?)null;
            foreach (var path in cat.Paths)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;

                try
                {
                    foreach (var file in EnumerateFiles(path, ct))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            if (cutoff.HasValue)
                            {
                                var fi = new FileInfo(file);
                                if (fi.LastWriteTimeUtc >= cutoff.Value) continue;
                            }
                            var len = SafeLength(file);
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            freed += len;
                            filesDeleted++;
                        }
                        catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
                    }
                    foreach (var dir in EnumerateDirectoriesDepthFirst(path, ct))
                    {
                        try { Directory.Delete(dir, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
            }
        }

        progress?.Report(new ScanProgress(total, total, "Done"));
        return new CleanupResult { BytesFreed = freed, FilesDeleted = filesDeleted, Errors = errors };
    }

    // ---------- IO helpers ----------

    private static long SafeLength(string path)
    { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var cur = stack.Pop();
            string[] files = Array.Empty<string>();
            string[] dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(cur); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { dirs = Directory.GetDirectories(cur); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push(d);
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesDepthFirst(string root, CancellationToken ct)
    {
        var all = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var cur = stack.Pop();
            string[] dirs = Array.Empty<string>();
            try { dirs = Directory.GetDirectories(cur); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var d in dirs) stack.Push(d);
            if (!string.Equals(cur, root, StringComparison.OrdinalIgnoreCase)) all.Add(cur);
        }
        all.Sort((a, b) => b.Length.CompareTo(a.Length));
        return all;
    }
}
