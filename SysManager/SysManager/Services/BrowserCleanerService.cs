// SysManager · BrowserCleanerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans and cleans per-browser cache / cookies / history / sessions for the Chromium
/// family (Chrome, Edge, Brave, Vivaldi, every Opera channel) and Firefox. Scan is read-only
/// (sizes only); Clean deletes only the discovered files. Cookies/sessions are flagged
/// sensitive and default to unselected so a clean never silently signs the user out.
///
/// The base data directories are injectable so the catalog/scan logic can be unit-tested
/// against a temp directory tree without touching the real browser profiles.
/// </summary>
public sealed class BrowserCleanerService
{
    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly BrowserProfiles _profiles;

    public BrowserCleanerService(string? localAppData = null, string? roamingAppData = null)
    {
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _profiles = new BrowserProfiles(_localAppData, _roamingAppData);
    }

    private sealed record Def(string Browser, string Category, string Description, bool Sensitive, string[] RelativePaths, bool Roaming = false);

    // Chrome/Edge/Brave keep per-profile data under "<UserData>\<profile>\..." in LocalAppData,
    // where <profile> is "Default" for the first profile and "Profile 1", "Profile 2", … for each
    // one the user adds. The profile segment used to be the literal "Default", so a second profile
    // (personal + work, or one per family member) was never scanned, never sized and never cleaned —
    // the tab reported a total that understated the real reclaimable space, and someone clearing
    // "browsing traces" kept every trace in their other profile. Profiles are now enumerated at scan
    // time, exactly as Firefox's already were (see BrowserProfiles).
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
    /// One <see cref="Def"/> set per Chromium profile that actually exists on disk, found by
    /// <see cref="BrowserProfiles.ChromiumProfiles"/> — the same walk the extension list uses (#1526). When the
    /// browser is not installed this yields nothing, so no rows appear.
    /// </summary>
    private IEnumerable<Def> ExpandChromiumDefs(string browser, string userDataRel)
    {
        foreach (var (dir, display, label) in _profiles.ChromiumProfiles(browser, userDataRel))
            foreach (var def in ChromiumDefs(display, userDataRel, $@"{userDataRel}\{dir}", label))
                yield return def;
    }

    // Opera is Chromium-based but does NOT use a "\Default\" profile segment: the profile lives
    // directly under "Opera Software\<channel>". It also splits its data across two roots — the
    // cache is under LocalAppData, but Cookies/History/Sessions live under Roaming AppData.
    // Routing it through ChromiumDefs pointed every path at a "\Default\" folder Opera never
    // creates, so scan/clean silently matched nothing.
    // NOTE: each Def's Roaming flag applies to ALL its RelativePaths, so cache paths (local)
    // and the roaming data paths must stay in separate Defs.
    private static Def[] OperaDefs(string channelFolder, string browser)
    {
        var profileRel = $@"Opera Software\{channelFolder}";
        return
        [
            // Cache lives under LocalAppData (Roaming: false).
            new(browser, "Cache", "Cached images and files.", false,
                [$@"{profileRel}\Cache", $@"{profileRel}\Code Cache", $@"{profileRel}\GPUCache"]),
            // Cookies/History/Sessions live under Roaming AppData (Roaming: true).
            new(browser, "History", "Browsing and download history.", false,
                [$@"{profileRel}\History", $@"{profileRel}\History-journal"], Roaming: true),
            new(browser, "Cookies", "Cookies — clearing these signs you out of websites.", true,
                [$@"{profileRel}\Network\Cookies", $@"{profileRel}\Network\Cookies-journal"], Roaming: true),
            new(browser, "Sessions", "Open tabs / session restore data.", true,
                [$@"{profileRel}\Sessions", $@"{profileRel}\Session Storage"], Roaming: true),
        ];
    }

    private List<Def> BuildDefs()
    {
        List<Def> defs = [];
        // Each Chromium browser can hold several profiles; every one that exists on disk is expanded
        // at scan time so a second profile's data is no longer invisible to this tab.
        foreach (var (browser, userDataRel) in BrowserProfiles.Chromium)
            defs.AddRange(ExpandChromiumDefs(browser, userDataRel));
        // Opera is the exception in the family (no "\Default\" segment, two roots), and it ships
        // several channels side by side — Opera GX among them. One Def set per channel.
        foreach (var (folder, browser) in BrowserProfiles.OperaChannels)
            defs.AddRange(OperaDefs(folder, browser));
        // Firefox keeps profiles in roaming AppData, but the cache lives under LocalAppData
        // in per-profile "<profile>\cache2" folders. We target the cache2 subfolders only —
        // never the Profiles root, which holds prefs.js, logins.json, key4.db and bookmarks.
        // The exact profile folder name is machine-specific, so the profiles are resolved at
        // scan time (see BrowserProfiles.FirefoxProfiles), which also decides the name each one's rows carry.
        var firefoxProfiles = _profiles.FirefoxProfiles();
        foreach (var (folder, display, label) in firefoxProfiles)
            defs.Add(new(display, "Cache", $"Cached images and files{label}.", false,
                [Path.Combine(BrowserProfiles.FirefoxProfilesRel, folder, "cache2")]));
        // Cookies and Sessions live under the ROAMING profile. Until now Firefox got a Cache row and
        // nothing else, so a Firefox user clearing "browsing traces" cleared none of them, while the
        // tab's header promised parity with the Chromium browsers. History is deliberately NOT offered:
        // Firefox stores history and BOOKMARKS in the same places.sqlite, so a "clear history" that
        // silently dropped bookmarks would be worse than the gap it fills. Chromium keeps them separate,
        // which is why History is safe there and not here.
        defs.AddRange(ExpandFirefoxDataDefs(firefoxProfiles));
        return defs;
    }

    /// <summary>
    /// One Cookies def and one Sessions def per Firefox profile, targeting SPECIFIC named files under
    /// the roaming profile — never the profile root, which holds <c>logins.json</c>, <c>key4.db</c>,
    /// <c>prefs.js</c> and <c>places.sqlite</c> (history AND bookmarks). Same safety invariant as
    /// <see cref="BrowserProfiles.FirefoxProfiles"/>, and the same roaming split Opera already uses.
    /// <para>Sensitive on both, so they are unticked by default and carry the "signs you out" badge, the
    /// same treatment the Chromium Cookies/Sessions rows get. History is intentionally absent — see
    /// <see cref="BuildDefs"/>.</para>
    /// </summary>
    private static IEnumerable<Def> ExpandFirefoxDataDefs((string Folder, string Display, string Label)[] profiles)
    {
        foreach (var (folder, display, label) in profiles)
        {
            var profileRel = Path.Combine(BrowserProfiles.FirefoxProfilesRel, folder);

            // Cookies: the sqlite database and its write-ahead/shared-memory sidecars. Named files
            // only — the profile root is never a target.
            yield return new(display, "Cookies",
                $"Cookies{label} — clearing these signs you out of websites.", true,
                [Path.Combine(profileRel, "cookies.sqlite"),
                 Path.Combine(profileRel, "cookies.sqlite-wal"),
                 Path.Combine(profileRel, "cookies.sqlite-shm")], Roaming: true);

            // Sessions: the current session file and the backups folder that restores open tabs.
            yield return new(display, "Sessions",
                $"Open tabs / session restore data{label}.", true,
                [Path.Combine(profileRel, "sessionstore.jsonlz4"),
                 Path.Combine(profileRel, "sessionstore-backups")], Roaming: true);
        }
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

            // A cancelled scan exits the loops above with partial results — they break on cancellation
            // rather than throwing — so returning normally handed the caller a short list with no way to
            // know it was short. The view model's success path then claimed a finished scan and, with
            // nothing found yet, said "No cleanable browser data found." (#2278). Throw so its existing
            // cancel branch runs; it already says "Cancelled." and was dead code until now.
            ct.ThrowIfCancellationRequested();

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

            // NOT ct.ThrowIfCancellationRequested() here, unlike the scan above, and the difference is
            // deliberate. This count is TRUE whether or not the run was cancelled — those files really were
            // deleted — so the caller reporting "Removed N file(s)" is not a false statement, only a silent
            // one about having stopped. Throwing would send the view model down its cancel branch, which
            // discards the count and skips the re-scan, so it would trade a missing word for missing
            // information. Saying "cancelled after removing N" needs the count to survive, which an
            // exception cannot carry (#2278).
            return deleted;
        }, ct);

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    private static (long size, int files) MeasurePath(string path, CancellationToken ct)
    {
        try
        {
            // Skip reparse-point leaves (a file/dir symlink or junction): following one
            // could measure — and later delete — data outside the browser's own tree.
            if (SafeFileWalk.IsReparsePoint(path)) return (0, 0);
            if (File.Exists(path)) return (SafeLength(path), 1);
            if (!Directory.Exists(path)) return (0, 0);
            long size = 0; var files = 0;
            foreach (var file in SafeFileWalk.Files(path, ct, ProfileWalk))
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
            if (SafeFileWalk.IsReparsePoint(path)) return 0;
            if (File.Exists(path))
            {
                if (TryDeleteFile(path)) deleted++;
                return deleted;
            }
            if (!Directory.Exists(path)) return 0;
            foreach (var file in SafeFileWalk.Files(path, ct, ProfileWalk))
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
    /// How a browser path is walked. No exclusions: a browser profile is never inside an extraction root.
    /// </summary>
    private static SafeWalkOptions ProfileWalk { get; } = new();
}
