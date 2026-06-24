// SysManager · BrowserCleanerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans and cleans per-browser cache / cookies / history / sessions for the Chromium
/// family (Chrome, Edge, Brave, Opera) and Firefox. Scan is read-only (sizes only); Clean
/// deletes only the discovered files. Cookies/sessions are flagged sensitive and default to
/// unselected so a clean never silently signs the user out.
///
/// The base data directories are injectable so the catalog/scan logic can be unit-tested
/// against a temp directory tree without touching the real browser profiles.
/// </summary>
public sealed class BrowserCleanerService
{
    private readonly string _localAppData;
    private readonly string _roamingAppData;

    public BrowserCleanerService(string? localAppData = null, string? roamingAppData = null)
    {
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    private sealed record Def(string Browser, string Category, string Description, bool Sensitive, string[] RelativePaths, bool Roaming = false);

    // Chromium "Default" profile layout is shared by Chrome/Edge/Brave/Opera.
    private static Def[] ChromiumDefs(string browser, string userDataRel) =>
    [
        new(browser, "Cache", "Cached images and files.", false,
            [$@"{userDataRel}\Default\Cache", $@"{userDataRel}\Default\Code Cache", $@"{userDataRel}\Default\GPUCache"]),
        new(browser, "History", "Browsing and download history.", false,
            [$@"{userDataRel}\Default\History", $@"{userDataRel}\Default\History-journal"]),
        new(browser, "Cookies", "Cookies — clearing these signs you out of websites.", true,
            [$@"{userDataRel}\Default\Network\Cookies", $@"{userDataRel}\Default\Network\Cookies-journal"]),
        new(browser, "Sessions", "Open tabs / session restore data.", true,
            [$@"{userDataRel}\Default\Sessions", $@"{userDataRel}\Default\Session Storage"]),
    ];

    private List<Def> BuildDefs()
    {
        List<Def> defs = [];
        defs.AddRange(ChromiumDefs("Google Chrome", @"Google\Chrome\User Data"));
        defs.AddRange(ChromiumDefs("Microsoft Edge", @"Microsoft\Edge\User Data"));
        defs.AddRange(ChromiumDefs("Brave", @"BraveSoftware\Brave-Browser\User Data"));
        defs.AddRange(ChromiumDefs("Opera", @"Opera Software\Opera Stable"));
        // Firefox keeps profiles in roaming AppData; cache lives in local.
        defs.Add(new("Firefox", "Cache", "Cached images and files.", false, [@"Mozilla\Firefox\Profiles"]) );
        return defs;
    }

    private string Root(bool roaming) => roaming ? _roamingAppData : _localAppData;

    /// <summary>
    /// Discovers cleanable items with their on-disk size. Read-only. Items whose paths don't
    /// exist (browser not installed / category empty) are omitted.
    /// </summary>
    public Task<IReadOnlyList<BrowserCleanupItem>> ScanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<BrowserCleanupItem>>(() =>
        {
            List<BrowserCleanupItem> items = [];
            foreach (var d in BuildDefs())
            {
                if (ct.IsCancellationRequested) break;
                var abs = d.RelativePaths
                    .Select(r => Path.Combine(Root(d.Roaming), r))
                    .Where(PathExists)
                    .ToArray();
                if (abs.Length == 0) continue;

                long size = 0; var files = 0;
                foreach (var p in abs)
                {
                    if (ct.IsCancellationRequested) break;
                    var (s, f) = MeasurePath(p, ct);
                    size += s; files += f;
                }
                if (size == 0 && files == 0) continue;

                items.Add(new BrowserCleanupItem
                {
                    Browser = d.Browser,
                    Category = d.Category,
                    Description = d.Description,
                    Paths = abs,
                    IsSensitive = d.Sensitive,
                    SizeBytes = size,
                    FileCount = files,
                    IsSelected = !d.Sensitive   // cache/history pre-selected; cookies/sessions opt-in
                });
            }
            return items;
        }, ct);

    /// <summary>
    /// Deletes the files for the given items. Returns the number of files deleted. Best-effort:
    /// locked files (browser running) are skipped, not fatal. Reparse points are never followed.
    /// </summary>
    public Task<int> CleanAsync(IReadOnlyList<BrowserCleanupItem> items, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var deleted = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var path in item.Paths)
                {
                    if (ct.IsCancellationRequested) break;
                    deleted += DeletePath(path, ct);
                }
            }
            Log.Information("BrowserCleaner: deleted {Count} files across {Items} items", deleted, items.Count);
            return deleted;
        }, ct);

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    private static (long size, int files) MeasurePath(string path, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path)) return (SafeLength(path), 1);
            if (!Directory.Exists(path) || IsReparsePoint(path)) return (0, 0);
            long size = 0; var files = 0;
            foreach (var file in SafeEnumerateFiles(path, ct))
            {
                if (ct.IsCancellationRequested) break;
                size += SafeLength(file);
                files++;
            }
            return (size, files);
        }
        catch (IOException) { return (0, 0); }
        catch (UnauthorizedAccessException) { return (0, 0); }
    }

    private static int DeletePath(string path, CancellationToken ct)
    {
        var deleted = 0;
        try
        {
            if (File.Exists(path))
            {
                if (TryDeleteFile(path)) deleted++;
                return deleted;
            }
            if (!Directory.Exists(path) || IsReparsePoint(path)) return 0;
            foreach (var file in SafeEnumerateFiles(path, ct))
            {
                if (ct.IsCancellationRequested) break;
                if (TryDeleteFile(file)) deleted++;
            }
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
        return deleted;
    }

    private static bool TryDeleteFile(string file)
    {
        try { File.Delete(file); return true; }
        catch (IOException) { return false; }                 // file locked (browser open)
        catch (UnauthorizedAccessException) { return false; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            var cur = stack.Pop();
            if (IsReparsePoint(cur)) continue;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(cur); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var d in subDirs) stack.Push(d);

            string[] files;
            try { files = Directory.GetFiles(cur); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
        }
    }
}
