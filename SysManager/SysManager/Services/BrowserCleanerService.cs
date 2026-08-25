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

    // Chrome/Edge/Brave keep per-profile data under "<UserData>\<profile>\..." in LocalAppData,
    // where <profile> is "Default" for the first profile and "Profile 1", "Profile 2", … for each
    // one the user adds. The profile segment used to be the literal "Default", so a second profile
    // (personal + work, or one per family member) was never scanned, never sized and never cleaned —
    // the tab reported a total that understated the real reclaimable space, and someone clearing
    // "browsing traces" kept every trace in their other profile. Profiles are now enumerated at scan
    // time, exactly as Firefox's already were (see ExpandFirefoxCachePaths).
    private static Def[] ChromiumDefs(string browser, string userDataRel, string profileRel, string profileLabel) =>
    [
        new(browser, "Cache", $"Cached images and files{profileLabel}.", false,
            [$@"{profileRel}\Cache", $@"{profileRel}\Code Cache", $@"{profileRel}\GPUCache"]),
        new(browser, "History", $"Browsing and download history{profileLabel}.", false,
            [$@"{profileRel}\History", $@"{profileRel}\History-journal"]),
        new(browser, "Cookies", $"Cookies{profileLabel} — clearing these signs you out of websites.", true,
            [$@"{profileRel}\Network\Cookies", $@"{profileRel}\Network\Cookies-journal"]),
        new(browser, "Sessions", $"Open tabs / session restore data{profileLabel}.", true,
            [$@"{profileRel}\Sessions", $@"{profileRel}\Session Storage"]),
    ];

    /// <summary>
    /// One <see cref="Def"/> set per Chromium profile that actually exists on disk.
    /// <para>
    /// Only "Default" and "Profile N" directories are considered — Chromium keeps plenty of other
    /// folders under <c>User Data</c> (<c>Crashpad</c>, <c>ShaderCache</c>, <c>System Profile</c>, …)
    /// and none of them are user profiles, so matching every subdirectory would point a delete at
    /// paths this tab never advertised. Reparse points are skipped and enumeration failures are
    /// swallowed, matching <see cref="ExpandFirefoxCachePaths"/>.
    /// </para>
    /// <para>
    /// When the browser is not installed this yields nothing, so no rows appear — the same outcome as
    /// before, since ScanAsync already omits paths that do not exist.
    /// </para>
    /// </summary>
    private IEnumerable<Def> ExpandChromiumDefs(string browser, string userDataRel)
    {
        var userDataAbs = Path.Combine(_localAppData, userDataRel);
        if (!Directory.Exists(userDataAbs) || IsReparsePoint(userDataAbs)) yield break;

        string[] candidates;
        try { candidates = Directory.GetDirectories(userDataAbs); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        // "Default" first, then Profile 1, Profile 2, … so the grid reads in a stable, predictable
        // order instead of whatever order the filesystem returned.
        foreach (var dir in candidates
                     .Select(Path.GetFileName)
                     .Where(name => !string.IsNullOrEmpty(name) && IsProfileFolder(name!))
                     .OrderBy(name => IsDefaultProfile(name!) ? 0 : 1)
                     .ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(Path.Combine(userDataAbs, dir!))) continue;

            // Name the profile in the Browser column so the user can see WHICH Chrome is being
            // cleaned — the thing a flat "Google Chrome" checkbox in other cleaners never tells her.
            // The default profile stays unlabelled, so the common single-profile case reads exactly
            // as it did before and no existing row text changes.
            var isDefault = IsDefaultProfile(dir!);
            var displayName = isDefault ? browser : $"{browser} — {dir}";
            var label = isDefault ? string.Empty : $" in {dir}";
            foreach (var def in ChromiumDefs(displayName, userDataRel, $@"{userDataRel}\{dir}", label))
                yield return def;
        }
    }

    private static bool IsDefaultProfile(string name) =>
        string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for "Default" and "Profile N" (N a positive integer) — the only directory names Chromium
    /// uses for user profiles.
    /// </summary>
    private static bool IsProfileFolder(string name)
    {
        if (IsDefaultProfile(name)) return true;
        if (!name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) return false;

        var suffix = name["Profile ".Length..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    // Opera Stable is Chromium-based but does NOT use a "\Default\" profile segment: the
    // profile lives directly under "Opera Software\Opera Stable". It also splits its data
    // across two roots — the cache is under LocalAppData, but Cookies/History/Sessions live
    // under Roaming AppData. Routing it through ChromiumDefs pointed every path at a
    // "\Default\" folder Opera never creates, so scan/clean silently matched nothing.
    // NOTE: each Def's Roaming flag applies to ALL its RelativePaths, so cache paths (local)
    // and the roaming data paths must stay in separate Defs.
    private static Def[] OperaDefs()
    {
        const string profileRel = @"Opera Software\Opera Stable";
        return
        [
            // Cache lives under LocalAppData (Roaming: false).
            new("Opera", "Cache", "Cached images and files.", false,
                [$@"{profileRel}\Cache", $@"{profileRel}\Code Cache", $@"{profileRel}\GPUCache"]),
            // Cookies/History/Sessions live under Roaming AppData (Roaming: true).
            new("Opera", "History", "Browsing and download history.", false,
                [$@"{profileRel}\History", $@"{profileRel}\History-journal"], Roaming: true),
            new("Opera", "Cookies", "Cookies — clearing these signs you out of websites.", true,
                [$@"{profileRel}\Network\Cookies", $@"{profileRel}\Network\Cookies-journal"], Roaming: true),
            new("Opera", "Sessions", "Open tabs / session restore data.", true,
                [$@"{profileRel}\Sessions", $@"{profileRel}\Session Storage"], Roaming: true),
        ];
    }

    private List<Def> BuildDefs()
    {
        List<Def> defs = [];
        // Each Chromium browser can hold several profiles; every one that exists on disk is expanded
        // at scan time so a second profile's data is no longer invisible to this tab.
        defs.AddRange(ExpandChromiumDefs("Google Chrome", @"Google\Chrome\User Data"));
        defs.AddRange(ExpandChromiumDefs("Microsoft Edge", @"Microsoft\Edge\User Data"));
        defs.AddRange(ExpandChromiumDefs("Brave", @"BraveSoftware\Brave-Browser\User Data"));
        defs.AddRange(OperaDefs());
        // Firefox keeps profiles in roaming AppData, but the cache lives under LocalAppData
        // in per-profile "<profile>\cache2" folders. We target the cache2 subfolders only —
        // never the Profiles root, which holds prefs.js, logins.json, key4.db and bookmarks.
        // The exact profile folder name is machine-specific, so the per-profile cache2 paths
        // are expanded at scan time (see ExpandFirefoxCachePaths).
        foreach (var cachePath in ExpandFirefoxCachePaths())
            defs.Add(new("Firefox", "Cache", "Cached images and files.", false, [cachePath]));
        // Cookies and Sessions live under the ROAMING profile. Until now Firefox got a Cache row and
        // nothing else, so a Firefox user clearing "browsing traces" cleared none of them, while the
        // tab's header promised parity with the Chromium browsers. History is deliberately NOT offered:
        // Firefox stores history and BOOKMARKS in the same places.sqlite, so a "clear history" that
        // silently dropped bookmarks would be worse than the gap it fills. Chromium keeps them separate,
        // which is why History is safe there and not here.
        defs.AddRange(ExpandFirefoxDataDefs());
        return defs;
    }

    /// <summary>
    /// One Cookies def and one Sessions def per Firefox profile, targeting SPECIFIC named files under
    /// the roaming profile — never the profile root, which holds <c>logins.json</c>, <c>key4.db</c>,
    /// <c>prefs.js</c> and <c>places.sqlite</c> (history AND bookmarks). Same safety invariant as
    /// <see cref="ExpandFirefoxCachePaths"/>, and the same roaming split Opera already uses.
    /// <para>Sensitive on both, so they are unticked by default and carry the "signs you out" badge, the
    /// same treatment the Chromium Cookies/Sessions rows get. History is intentionally absent — see
    /// <see cref="BuildDefs"/>.</para>
    /// </summary>
    private IEnumerable<Def> ExpandFirefoxDataDefs()
    {
        const string profilesRel = @"Mozilla\Firefox\Profiles";
        var profilesAbs = Path.Combine(_roamingAppData, profilesRel);
        if (!Directory.Exists(profilesAbs) || IsReparsePoint(profilesAbs)) yield break;

        string[] profileDirs;
        try { profileDirs = Directory.GetDirectories(profilesAbs); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        // Project each profile dir to its relative path up front, so the loop body just yields the
        // two defs it builds from that path (and CodeQL's missed-select does not flag the map).
        foreach (var profileRel in profileDirs.Select(d => Path.Combine(profilesRel, Path.GetFileName(d))))
        {
            // Cookies: the sqlite database and its write-ahead/shared-memory sidecars. Named files
            // only — the profile root is never a target.
            yield return new("Firefox", "Cookies",
                "Cookies — clearing these signs you out of websites.", true,
                [Path.Combine(profileRel, "cookies.sqlite"),
                 Path.Combine(profileRel, "cookies.sqlite-wal"),
                 Path.Combine(profileRel, "cookies.sqlite-shm")], Roaming: true);

            // Sessions: the current session file and the backups folder that restores open tabs.
            yield return new("Firefox", "Sessions",
                "Open tabs / session restore data.", true,
                [Path.Combine(profileRel, "sessionstore.jsonlz4"),
                 Path.Combine(profileRel, "sessionstore-backups")], Roaming: true);
        }
    }

    /// <summary>
    /// Returns the relative paths of each Firefox profile's <c>cache2</c> folder under
    /// LocalAppData (e.g. <c>Mozilla\Firefox\Profiles\abc.default-release\cache2</c>).
    /// Returns an empty sequence when Firefox isn't installed. Never returns the Profiles
    /// root, so a clean can only ever touch cache, never saved logins/bookmarks/prefs.
    /// </summary>
    private IEnumerable<string> ExpandFirefoxCachePaths()
    {
        const string profilesRel = @"Mozilla\Firefox\Profiles";
        var profilesAbs = Path.Combine(_localAppData, profilesRel);
        if (!Directory.Exists(profilesAbs) || IsReparsePoint(profilesAbs)) yield break;

        string[] profileDirs;
        try { profileDirs = Directory.GetDirectories(profilesAbs); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var dir in profileDirs)
            yield return Path.Combine(profilesRel, Path.GetFileName(dir), "cache2");
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
            // Skip reparse-point leaves (a file/dir symlink or junction): following one
            // could measure — and later delete — data outside the browser's own tree.
            if (IsReparsePoint(path)) return (0, 0);
            if (File.Exists(path)) return (SafeLength(path), 1);
            if (!Directory.Exists(path)) return (0, 0);
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
            // Skip reparse-point leaves before any delete. File.Delete on a file symlink
            // removes the link, but a junction standing in for an expected directory leaf
            // would otherwise be recursed into and its target's files deleted — data loss
            // outside the browser tree. Fail-closed IsReparsePoint is the gate (see below).
            if (IsReparsePoint(path)) return 0;
            if (File.Exists(path))
            {
                if (TryDeleteFile(path)) deleted++;
                return deleted;
            }
            if (!Directory.Exists(path)) return 0;
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

    /// <summary>
    /// True when the path is a reparse point (junction or symbolic link). Fails SAFE:
    /// returns true when the attributes can't be read, so an unreadable entry is treated
    /// as a link and skipped rather than followed/deleted. Mirrors
    /// <see cref="DeepCleanupService"/> and <see cref="FileShredderService"/>.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
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
