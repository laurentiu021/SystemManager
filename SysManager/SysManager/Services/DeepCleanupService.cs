// SysManager · DeepCleanupService — safe-by-design scanner & cleaner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Enumeration;
using Serilog;
using SysManager.Helpers;
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
    private readonly ICleanupRoots _roots;

    /// <summary>
    /// Builds a service that scans the real machine.
    /// </summary>
    public DeepCleanupService() : this(new SystemCleanupRoots())
    {
    }

    /// <summary>
    /// Builds a service that scans <paramref name="roots"/>, so a test can supply a tree it owns.
    /// </summary>
    /// <remarks>
    /// The parameterless overload above is what production uses, and it passes the same roots the service
    /// read inline before this seam existed — so the default behaviour is unchanged by construction
    /// rather than by inspection (#2176).
    /// </remarks>
    public DeepCleanupService(ICleanupRoots roots)
        => _roots = roots ?? throw new ArgumentNullException(nameof(roots));

    public sealed record ScanProgress(int Current, int Total, string CategoryName);

    public Task<IReadOnlyList<CleanupCategory>> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(_roots, progress, ct), ct);

    public Task<CleanupResult> CleanAsync(
        IReadOnlyList<CleanupCategory> categories,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Clean(categories, progress, ct), ct);

    // ---------- scan definitions (built once, then iterated with progress) ----------

    /// <summary>One scannable bucket: what to call it, what to tell the user about it, and where it lives.</summary>
    /// <param name="Paths">
    /// Directories to walk. An entry may also name a single FILE — <c>MEMORY.DMP</c> is one file sitting
    /// directly in the Windows folder, and naming its parent instead would walk all of <c>%WinDir%</c>.
    /// </param>
    /// <param name="FilePatterns">
    /// When set, only files matching one of these wildcards are counted and deleted, and the bucket's
    /// emptied folders are left in place. Every bucket but two owns its whole folder, so the default is
    /// null and means "everything under the path" — see <see cref="Scan"/> for why both halves matter.
    /// </param>
    private sealed record Def(
        string Name,
        string Description,
        string[] Paths,
        string[]? FilePatterns = null,
        TimeSpan? OlderThan = null,
        bool IsDestructiveHint = false,
        bool IsRecycleBin = false);

    private static List<Def> BuildDefinitions(ICleanupRoots roots)
    {
        var localAppData = roots.LocalAppData;
        var programData = roots.ProgramData;
        var systemDrive = roots.SystemDrive;
        var windowsDir = roots.WindowsDirectory;
        var tempUser = roots.UserTemp;
        var pfx86 = roots.ProgramFilesX86;
        var pf = roots.ProgramFiles;

        var defs = new List<Def>
        {
            new("NVIDIA installer leftovers",
                "Extracted driver packages NVIDIA drops on your drive root and in ProgramData during an install. Safe to remove once the driver is installed.",
                [
                    Path.Combine(systemDrive, "NVIDIA"),
                    Path.Combine(programData, "NVIDIA Corporation", "Downloader"),
                    Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"),
                    Path.Combine(programData, "NVIDIA Corporation", "Installer2"),
                    Path.Combine(localAppData, "NVIDIA", "GLCache"),
                    Path.Combine(localAppData, "NVIDIA", "DXCache"),
                    Path.Combine(localAppData, "NVIDIA", "ComputeCache"),
                ]),

            new("AMD installer leftovers",
                "Unpacked driver installer folder AMD creates on the root of C:\\. Confirmed safe by AMD community docs.",
                [Path.Combine(systemDrive, "AMD")]),

            new("Intel driver extracts",
                "Temporary driver package extracts from Intel installers.",
                [Path.Combine(systemDrive, "Intel")]),

            new("Windows Update cache",
                "Previously downloaded Windows Update packages. Windows re-downloads anything it still needs next time.",
                [Path.Combine(windowsDir, "SoftwareDistribution", "Download")]),

            new("Delivery Optimization cache",
                "Peer-to-peer update cache. Regenerated on demand.",
                [Path.Combine(windowsDir, "SoftwareDistribution", "DeliveryOptimization", "Cache")]),

            new("Windows Installer patch cache",
                "C:\\Windows\\Installer\\$PatchCache$ stores baseline patch files used only when uninstalling an MSI patch. Safe per Microsoft devblog.",
                [Path.Combine(windowsDir, "Installer", "$PatchCache$")]),

            new("Temporary files",
                "Per-user and system TEMP folders. Files an app is holding open are skipped, and the "
                + "folder apps unpack themselves into is left alone entirely.",
                [tempUser, Path.Combine(windowsDir, "Temp")]),

            new("Prefetch files",
                "Windows boot/launch prefetch cache. Windows rebuilds it as apps are used.",
                [Path.Combine(windowsDir, "Prefetch")]),

            new("Crash dumps & error reports",
                "Windows Error Reporting queue and user-mode crash dumps (*.dmp).",
                [
                    Path.Combine(localAppData, "CrashDumps"),
                    Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                ]),

            new("Blue-screen memory dumps",
                "What Windows writes out when it blue-screens. MEMORY.DMP is sized to how much RAM you have, "
                + "so on a machine that has crashed it is often the single biggest file on the drive. These are "
                + "also the only record of why it crashed — delete them and any investigation into that ends, "
                + "which is why this one is never ticked for you. Deleting them needs administrator.",
                [
                    Path.Combine(windowsDir, "MEMORY.DMP"),
                    Path.Combine(windowsDir, "Minidump"),
                    Path.Combine(windowsDir, "LiveKernelReports"),
                ],
                FilePatterns: ["*.dmp"],
                IsDestructiveHint: true),

            new("Explorer thumbnail & icon cache",
                "The picture previews and icons Windows keeps so folders open quickly. It rebuilds them as you "
                + "browse, and clearing them is the standard fix for thumbnails that show the wrong picture or "
                + "come up blank.",
                [Path.Combine(localAppData, "Microsoft", "Windows", "Explorer")],
                FilePatterns: ["thumbcache_*.db", "iconcache_*.db"]),

            new("Old Windows servicing logs (> 30 days)",
                "CBS logs older than 30 days. Windows keeps rolling ones itself.",
                [Path.Combine(windowsDir, "Logs", "CBS")],
                OlderThan: TimeSpan.FromDays(30)),

            new("DirectX shader cache",
                "Precompiled GPU shaders cached by Windows. Rebuilt automatically the next time games run — clearing can fix stutter.",
                [Path.Combine(localAppData, "D3DSCache")]),

            new("Recycle Bin (all drives)",
                "Emptying the recycle bin on every fixed drive.",
                [.. roots.RecycleBinPaths],
                IsRecycleBin: true),

            new("Steam — browser & depot cache",
                "Steam web browser cache, HTML cache, app cache and depot lookup cache. Doesn't touch game files, downloads or logins.",
                SteamCacheDirs(roots)),

            new("Steam — shader cache",
                "Per-game shader cache under steamapps\\shadercache. Rebuilt on next launch — clearing can fix stutter or shader corruption.",
                SteamShaderCacheDirs(roots)),

            new("Epic Games Launcher — webcache & logs",
                "Epic Launcher browser webcache and log files. Doesn't affect your Epic login or installed games.",
                [
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache_4147"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "webcache_4430"),
                    Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "Logs"),
                    Path.Combine(localAppData, "UnrealEngineLauncher", "Saved", "webcache"),
                ]),

            new("Battle.net — cache",
                "Battle.net agent and Blizzard launcher cache. Doesn't touch installed games or logins.",
                [
                    Path.Combine(programData, "Battle.net", "Agent", "data", "cache"),
                    Path.Combine(programData, "Blizzard Entertainment", "Battle.net", "Cache"),
                    Path.Combine(localAppData, "Battle.net", "Cache"),
                ]),

            new("Riot Client / League of Legends — logs",
                "Riot Client and League client logs only. No game files or credentials.",
                RiotLogDirs(roots)),

            new("GOG Galaxy — cache",
                "GOG Galaxy launcher webcache and redists installer cache.",
                [
                    Path.Combine(localAppData, "GOG.com", "Galaxy", "webcache"),
                    Path.Combine(programData, "GOG.com", "Galaxy", "redists"),
                ]),

            new("EA App / Origin — cache",
                "EA Desktop (and legacy Origin) browser cache and logs. Doesn't affect installed games or logins.",
                [
                    Path.Combine(localAppData, "Electronic Arts", "EA Desktop", "CEF-Cache"),
                    Path.Combine(localAppData, "Electronic Arts", "EA Desktop", "Logs"),
                    Path.Combine(localAppData, "Origin", "Logs"),
                    Path.Combine(programData, "Origin", "Logs"),
                ]),
        };

        // Windows.old — optional, never auto-selected
        var windowsOld = Path.Combine(systemDrive, "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            defs.Add(new Def(
                "Windows.old (previous Windows installation)",
                "Remove only if you're sure you don't want to roll back to your previous Windows version. Windows normally auto-deletes this after 10 days.",
                [windowsOld],
                IsDestructiveHint: true));
        }

        return defs;
    }

    // ---------- scanning ----------

    private static IReadOnlyList<CleanupCategory> Scan(
        ICleanupRoots roots, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var defs = BuildDefinitions(roots);
        var results = new List<CleanupCategory>(defs.Count);
        var total = defs.Count;

        for (var i = 0; i < defs.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var d = defs[i];
            progress?.Report(new ScanProgress(i + 1, total, d.Name));

            var existing = d.Paths.Where(p => !string.IsNullOrEmpty(p) && (Directory.Exists(p) || File.Exists(p)))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            long size = 0; var files = 0; var skipped = 0;
            var cutoff = d.OlderThan.HasValue ? DateTime.UtcNow - d.OlderThan.Value : (DateTime?)null;

            foreach (var p in existing)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var file in EnumerateTargets(p, d.FilePatterns, ct))
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
                    catch (IOException) { skipped++; }
                    catch (UnauthorizedAccessException) { skipped++; }
                    catch (System.Security.SecurityException) { skipped++; }
                }
            }

            results.Add(new CleanupCategory
            {
                Name = d.Name,
                Description = d.Description,
                Paths = existing,
                TotalSizeBytes = size,
                FileCount = files,
                SkippedCount = skipped,
                OlderThan = d.OlderThan,
                // Carried on the category, not looked up from the definitions at clean time: Clean is
                // handed categories by the caller and walks their Paths, so a filter it could not see
                // would give the user an honest size on screen and a delete that took the whole folder.
                FilePatterns = d.FilePatterns,
                IsDestructiveHint = d.IsDestructiveHint,
                IsRecycleBin = d.IsRecycleBin,
                IsSelected = size > 0 && !d.IsDestructiveHint
            });
        }

        // A cancelled scan exits the loops above with partial results; reporting "Done" and
        // returning them would let the caller show a success toast for a cancelled op. Throw
        // so the ViewModel's OperationCanceledException arm reports "cancelled" instead.
        // Matches LargeFileScanner.
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(total, total, "Done"));
        return results;
    }

    // ---------- launcher roots ----------

    private static string[] SteamRoots(ICleanupRoots roots)
    {
        List<string> found =
        [
            Path.Combine(roots.ProgramFilesX86, "Steam"),
            Path.Combine(roots.ProgramFiles, "Steam"),
        ];
        foreach (var driveRoot in roots.FixedDriveRoots)
        {
            var candidate = Path.Combine(driveRoot, "Steam");
            if (Directory.Exists(candidate)) found.Add(candidate);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SteamCacheDirs(ICleanupRoots roots)
    {
        List<string> result = [];
        foreach (var root in SteamRoots(roots))
        {
            result.Add(Path.Combine(root, "appcache"));
            result.Add(Path.Combine(root, "htmlcache"));
            result.Add(Path.Combine(root, "depotcache"));
            result.Add(Path.Combine(root, "logs"));
        }
        result.Add(Path.Combine(roots.LocalAppData, "Steam", "htmlcache"));
        return result.ToArray();
    }

    private static string[] SteamShaderCacheDirs(ICleanupRoots roots)
    {
        List<string> result = [];
        foreach (var root in SteamRoots(roots))
            result.Add(Path.Combine(root, "steamapps", "shadercache"));
        foreach (var driveRoot in roots.FixedDriveRoots)
        {
            var candidate = Path.Combine(driveRoot, "SteamLibrary", "steamapps", "shadercache");
            if (Directory.Exists(candidate)) result.Add(candidate);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Riot Client logs are in %LOCALAPPDATA%, but League of Legends can be
    /// installed on any drive. Scan all fixed drives for Riot Games folders.
    /// </summary>
    private static string[] RiotLogDirs(ICleanupRoots roots)
    {
        var result = new List<string>
        {
            Path.Join(roots.LocalAppData, "Riot Games", "Riot Client", "Logs"),
            Path.Join(roots.ProgramFilesX86, "Riot Games", "League of Legends", "Logs"),
            Path.Join(roots.ProgramFiles, "Riot Games", "League of Legends", "Logs"),
        };
        foreach (var driveRoot in roots.FixedDriveRoots)
        {
            var candidate = Path.Join(driveRoot, "Riot Games", "League of Legends", "Logs");
            result.Add(candidate);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------- cleaning ----------

    private static CleanupResult Clean(IReadOnlyList<CleanupCategory> categories, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long freed = 0;
        List<string> errors = [];
        var filesDeleted = 0;
        var selected = categories.Where(c => c.IsSelected).ToList();
        var total = selected.Count;

        for (var idx = 0; idx < selected.Count; idx++)
        {
            if (ct.IsCancellationRequested) break;
            var cat = selected[idx];
            progress?.Report(new ScanProgress(idx + 1, total, "Cleaning " + cat.Name));

            // The Recycle Bin is not an ordinary folder: each `$Recycle.Bin\<SID>`
            // holds paired $I/$R index/data files plus a system desktop.ini, and the
            // shell tracks bin state in its own structures. Deleting those files
            // directly — and removing the per-SID folder out from under the shell —
            // leaves the bin inconsistent (ghost/undeletable items in Explorer).
            // Empty it through the documented shell API instead, matching TuneUp.
            if (cat.IsRecycleBin)
            {
                var sizeBefore = cat.TotalSizeBytes;
                if (RecycleBinHelper.EmptyAllDrives())
                {
                    freed += sizeBefore;
                    filesDeleted += cat.FileCount;
                }
                else
                {
                    errors.Add($"{cat.Name}: the Recycle Bin could not be emptied.");
                }
                continue;
            }

            var cutoff = cat.OlderThan.HasValue ? DateTime.UtcNow - cat.OlderThan.Value : (DateTime?)null;
            var patterns = cat.FilePatterns?.ToArray();
            foreach (var path in cat.Paths)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path))) continue;

                try
                {
                    foreach (var file in EnumerateTargets(path, patterns, ct))
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
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            errors.Add($"{file}: {ex.Message}");
                            Log.Debug(ex, "Deep cleanup: failed to delete file {File}", file);
                        }
                    }
                    // A filtered bucket owns files, not folders: the Explorer cache lives inside Explorer's
                    // own working folder, and removing a directory there — which the bucket's scan never
                    // counted — is outside what the user agreed to.
                    if (patterns is not null) continue;
                    foreach (var dir in EnumerateDirectoriesDepthFirst(path, ct, SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory))
                    {
                        try { Directory.Delete(dir, recursive: false); }
                        catch (IOException ex) { Log.Debug(ex, "Deep cleanup: failed to delete directory {Dir}", dir); }
                        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Deep cleanup: access denied deleting directory {Dir}", dir); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    errors.Add($"{path}: {ex.Message}");
                    Log.Debug(ex, "Deep cleanup: failed to enumerate path {Path}", path);
                }
            }
        }

        // A cancelled clean stops mid-way with partial deletions (already done + logged);
        // reporting "Done" + returning a result would fire a success toast for a cancelled
        // op. Throw so the ViewModel reports "Clean cancelled" instead. The work already
        // performed is real and logged — throwing only changes the outcome message.
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgress(total, total, "Done"));
        return new CleanupResult { BytesFreed = freed, FilesDeleted = filesDeleted, Errors = errors };
    }

    // ---------- IO helpers ----------

    private static long SafeLength(string path)
    { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }

    /// <summary>
    /// Yields the files one <see cref="Def.Paths"/> entry stands for: the file itself when the entry names
    /// a file, otherwise everything under it — in both cases narrowed to <paramref name="patterns"/> when
    /// the definition set any.
    /// </summary>
    /// <remarks>
    /// Two behaviours the bucket definitions need and the raw walk does not give. A path may be a FILE,
    /// because <c>MEMORY.DMP</c> sits directly in <c>%WinDir%</c> and pointing the bucket at that folder
    /// would walk all of Windows. And a walk may need narrowing, because
    /// <c>%LOCALAPPDATA%\Microsoft\Windows\Explorer</c> holds the rebuildable thumbnail caches next to the
    /// jump lists that are the user's recent-files history.
    /// <para>Called by BOTH <see cref="Scan"/> and <see cref="Clean"/>, so the set of files a bucket
    /// reports is by construction the set it deletes. That is the whole point of the helper: those were
    /// two separate walks, and a filter added to one of them is a bucket that lies.</para>
    /// </remarks>
    private static IEnumerable<string> EnumerateTargets(string path, string[]? patterns, CancellationToken ct)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            // Guarded like a traversal root: a symlink here would make the caller delete its target,
            // outside the bucket's tree. IsReparsePoint fails safe on an access error.
            if (!IsReparsePoint(path) && Matches(path, patterns)) yield return path;
            yield break;
        }

        foreach (var file in EnumerateFiles(path, ct, SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory))
        {
            if (Matches(file, patterns)) yield return file;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/>'s file name matches one of <paramref name="patterns"/>, or when
    /// the bucket set none — an unfiltered bucket owns everything under its paths.
    /// </summary>
    /// <remarks>
    /// <see cref="FileSystemName.MatchesSimpleExpression(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)"/>
    /// rather than a hand-rolled comparison, so <c>thumbcache_*.db</c> means here exactly what it means to
    /// <c>Directory.EnumerateFiles</c>. Case-insensitive: NTFS is, and Windows writes <c>MEMORY.DMP</c>
    /// upper-case while the pattern reads lower.
    /// </remarks>
    private static bool Matches(string path, string[]? patterns)
    {
        if (patterns is null || patterns.Length == 0) return true;
        var name = Path.GetFileName(path.AsSpan());
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks <paramref name="root"/> depth-first and yields every file under it, skipping reparse points
    /// and the excluded subtrees. Enumeration errors skip that directory rather than abort the walk.
    /// </summary>
    /// <param name="root">Directory to walk. Yields nothing if it is itself a reparse point or excluded.</param>
    /// <param name="ct">Stops the walk between directories; a cancelled walk simply ends.</param>
    /// <param name="excludeSubtrees">
    /// Directory trees to skip entirely. Callers pass <see cref="SystemPaths.BundleExtractionRoot"/> and
    /// <see cref="SystemPaths.OwnExtractionDirectory"/>: the "Temporary files" definition covers all of
    /// %TEMP%, which is where every single-file .NET app unpacks its native libraries. Deleting those made
    /// a clean run report errors (loaded files refuse to delete) and broke a later lazy load for anything
    /// unpacked but not yet opened — and excluding only this process's own leaf did that to every OTHER
    /// app under the same root.
    /// </param>
    private static IEnumerable<string> EnumerateFiles(
        string root, CancellationToken ct, params string?[] excludeSubtrees)
    {
        // Guard the traversal ROOT itself, not just its children: if a cleanup-root
        // cache path is replaced by a junction/symlink (writable without admin, e.g.
        // %LOCALAPPDATA%\NVIDIA\GLCache), EnumerateFiles(root) would yield the LINK
        // TARGET's files and the caller would delete them — data loss outside the
        // target tree. IsReparsePoint fails safe (returns true on access error).
        if (IsReparsePoint(root) || SystemPaths.IsInsideAnySubtree(root, excludeSubtrees)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var cur = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try { files = Directory.EnumerateFiles(cur); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            try { dirs = Directory.EnumerateDirectories(cur); } catch (IOException) { dirs = []; } catch (UnauthorizedAccessException) { dirs = []; }

            // Wrap iteration because the enumerator can throw from MoveNext() rather than from the call
            // above — a directory that stops being one mid-walk, or an entry that becomes unreadable.
            //
            // The throw ENDS this directory; it does not skip one entry. A throwing MoveNext leaves the
            // enumerator terminal, so `continue` here re-threw the same exception forever and pinned a core
            // in a loop cancellation cannot even reach (the ct check is on the outer loop). Measured, not
            // reasoned: EnumerateFiles over a path that is a FILE returns fine, GetEnumerator returns fine,
            // and MoveNext then threw IOException on all ten attempts without advancing once. `break` moves
            // on to the next directory on the stack, which is the most this walk can honestly do.
            using var enumerator = files.GetEnumerator();
            while (true)
            {
                string? item;
                try { if (!enumerator.MoveNext()) break; item = enumerator.Current; }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
                yield return item;
            }

            // Never descend into reparse points (junctions / symbolic links):
            // following one could enumerate — and the caller could then delete —
            // files that live outside the cleanup target tree (data-loss risk).
            foreach (var d in dirs)
            {
                if (!IsReparsePoint(d) && !SystemPaths.IsInsideAnySubtree(d, excludeSubtrees)) stack.Push(d);
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesDepthFirst(
        string root, CancellationToken ct, params string?[] excludeSubtrees)
    {
        List<string> all = [];
        // Guard the root (see EnumerateFiles): a junction at the root must not be
        // traversed, or its target's subdirectories could be reached for deletion.
        if (IsReparsePoint(root) || SystemPaths.IsInsideAnySubtree(root, excludeSubtrees)) return all;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var cur = stack.Pop();
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(cur); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            // Skip reparse points: deleting the target's contents (or even the
            // link recursively) could reach outside the cleanup tree.
            foreach (var d in dirs)
            {
                if (!IsReparsePoint(d) && !SystemPaths.IsInsideAnySubtree(d, excludeSubtrees)) stack.Push(d);
            }
            if (!string.Equals(cur, root, StringComparison.OrdinalIgnoreCase)) all.Add(cur);
        }
        all.Sort((a, b) => b.Length.CompareTo(a.Length));
        return all;
    }

    /// <summary>
    /// True when the directory is a reparse point (junction or symbolic link).
    /// Such entries are skipped during traversal so cleanup can never follow a
    /// link out of the target tree and delete unrelated user data.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // Recycle Bin is emptied via the shared RecycleBinHelper (shell API, not raw file
    // delete) so the SHEmptyRecycleBin interop has a single source of truth.
}
