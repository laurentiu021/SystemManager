// SysManager · BrowserCleanerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BrowserCleanerService"/>. Runs against temp LOCALAPPDATA/APPDATA
/// trees so scan + clean can be exercised deterministically without touching real browser
/// profiles. Verifies that only existing categories surface, sizes are measured, cookies are
/// flagged sensitive + unselected, and Clean actually removes files.
/// </summary>
public sealed class BrowserCleanerServiceTests : IDisposable
{
    private readonly string _local;
    private readonly string _roaming;
    private readonly BrowserCleanerService _svc;

    public BrowserCleanerServiceTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "SysManagerBrowserTest_" + Guid.NewGuid().ToString("N"));
        _local = Path.Combine(baseDir, "Local");
        _roaming = Path.Combine(baseDir, "Roaming");
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_roaming);
        _svc = new BrowserCleanerService(_local, _roaming);
    }

    public void Dispose()
    {
        var parent = Directory.GetParent(_local)!.FullName;
        if (!Directory.Exists(parent)) return;
        // Remove any junctions/symlinks as links first — Directory.Delete(recursive:true)
        // throws "The parameter is incorrect" on reparse points (the junctions some tests
        // create). Unlink them (non-recursively, so the target is untouched), then delete.
        try { UnlinkReparsePoints(parent); } catch (IOException) { /* best-effort teardown */ }
        try { Directory.Delete(parent, recursive: true); } catch (IOException) { /* best-effort teardown */ }
    }

    private static void UnlinkReparsePoints(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                Directory.Delete(sub);            // remove the link only, never its target
            else
                UnlinkReparsePoints(sub);
        }
    }

    private void WriteFile(string relUnderLocal, int bytes)
    {
        var full = Path.Combine(_local, relUnderLocal);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
    }

    private void WriteRoamingFile(string relUnderRoaming, int bytes)
    {
        var full = Path.Combine(_roaming, relUnderRoaming);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
    }

    [Fact]
    public async Task Scan_NoBrowsers_ReturnsEmpty()
        => Assert.Empty(await _svc.ScanAsync());

    [Fact]
    public async Task Scan_FindsChromeCache_WithSize()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1024);
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_1", 2048);

        var items = await _svc.ScanAsync();
        var cache = items.FirstOrDefault(i => i.Browser == "Google Chrome" && i.Category == "Cache");
        Assert.NotNull(cache);
        Assert.Equal(3072, cache!.SizeBytes);
        Assert.Equal(2, cache.FileCount);
        Assert.True(cache.IsSelected);      // cache is pre-selected
        Assert.False(cache.IsSensitive);
    }

    [Fact]
    public async Task Scan_CookiesAreSensitive_AndUnselected()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Network\Cookies", 512);

        var items = await _svc.ScanAsync();
        var cookies = items.FirstOrDefault(i => i.Category == "Cookies");
        Assert.NotNull(cookies);
        Assert.True(cookies!.IsSensitive);
        Assert.False(cookies.IsSelected);   // never auto-selected — would sign the user out
    }

    [Fact]
    public async Task Scan_OmitsEmptyOrMissingCategories()
    {
        // Only Edge cache exists; nothing else should surface.
        WriteFile(@"Microsoft\Edge\User Data\Default\Cache\f", 10);
        var items = await _svc.ScanAsync();
        Assert.All(items, i => Assert.Equal("Microsoft Edge", i.Browser));
        Assert.Contains(items, i => i.Category == "Cache");
        Assert.DoesNotContain(items, i => i.Category == "Cookies");
    }

    [Fact]
    public async Task Clean_DeletesSelectedFiles()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1024);
        WriteFile(@"Google\Chrome\User Data\Default\Cache\sub\data_1", 2048);

        var items = await _svc.ScanAsync();
        var cache = items.First(i => i.Category == "Cache");
        var deleted = await _svc.CleanAsync([cache]);

        Assert.Equal(2, deleted);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(_local, @"Google\Chrome\User Data\Default\Cache"), "*", SearchOption.AllDirectories));
    }

    // --- Reparse-point safety: a standard user can drop a junction (mklink /J, no admin)
    // inside a browser profile dir. The cleaner must NEVER follow it out of the tree to
    // measure or delete unrelated user data. ---

    /// <summary>Creates an NTFS junction at <paramref name="linkPath"/> → <paramref name="targetPath"/>.</summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(linkPath);
    }

    [Fact]
    public async Task Scan_DoesNotFollowJunction_OutOfBrowserTree()
    {
        // "victim" data living OUTSIDE the browser tree.
        var victimDir = Path.Combine(Directory.GetParent(_local)!.FullName, "victim");
        Directory.CreateDirectory(victimDir);
        File.WriteAllBytes(Path.Combine(victimDir, "secret.dat"), new byte[4096]);

        // Replace Chrome's Cache leaf with a junction pointing at the victim dir.
        var cacheLink = Path.Combine(_local, @"Google\Chrome\User Data\Default\Cache");
        if (!TryCreateJunction(cacheLink, victimDir)) return; // skip if junctions unavailable

        var items = await _svc.ScanAsync();
        var cache = items.FirstOrDefault(i => i.Browser == "Google Chrome" && i.Category == "Cache");

        // The reparse-point guard makes the junctioned cache measure as (0 bytes, 0 files),
        // so the scan drops the empty category entirely (ScanAsync skips size==0 && files==0).
        // Either way it must NEVER surface with the victim's 4096 bytes — a followed junction
        // would show up here as a non-zero Cache item. Asserting null is correct-by-design.
        Assert.Null(cache);

        // No scanned item anywhere may have absorbed the victim's bytes through the junction.
        Assert.DoesNotContain(items, i => i.SizeBytes >= 4096);

        // And the out-of-tree victim data is untouched by the scan.
        Assert.True(File.Exists(Path.Combine(victimDir, "secret.dat")));
    }

    [Fact]
    public async Task Clean_DoesNotDeleteThroughJunction_OutOfBrowserTree()
    {
        var victimDir = Path.Combine(Directory.GetParent(_local)!.FullName, "victim");
        Directory.CreateDirectory(victimDir);
        var secret = Path.Combine(victimDir, "secret.dat");
        File.WriteAllBytes(secret, new byte[4096]);

        var cacheLink = Path.Combine(_local, @"Google\Chrome\User Data\Default\Cache");
        if (!TryCreateJunction(cacheLink, victimDir)) return; // skip if junctions unavailable

        // Force the item through Clean directly (bypassing scan-time filtering) to prove
        // the deletion path itself refuses to follow the junction.
        var item = new Models.BrowserCleanupItem
        {
            Browser = "Google Chrome",
            Category = "Cache",
            Description = "",
            Paths = [cacheLink],
            IsSensitive = false,
            IsSelected = true
        };
        var deleted = await _svc.CleanAsync([item]);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(secret), "Clean must not delete files through a junction.");
    }

    // --- Opera: Chromium-based but no "\Default\" profile segment, and its Cookies/History/
    // Sessions live under Roaming (only Cache is under Local). Regression for the silent no-op
    // where Opera was routed through ChromiumDefs (all paths under a non-existent \Default\). ---

    [Fact]
    public async Task Scan_FindsOperaCache_UnderLocal_NoDefaultSegment()
    {
        // Real Opera layout: cache directly under "Opera Software\Opera Stable\Cache" in Local,
        // with NO "\Default\" segment.
        WriteFile(@"Opera Software\Opera Stable\Cache\data_0", 4096);

        var items = await _svc.ScanAsync();
        var cache = items.FirstOrDefault(i => i.Browser == "Opera" && i.Category == "Cache");
        Assert.NotNull(cache);                       // was null before the fix (wrong \Default\ path)
        Assert.Equal(4096, cache!.SizeBytes);
        Assert.False(cache.IsSensitive);
        Assert.All(cache.Paths, p => Assert.DoesNotContain(@"\Default\", p));
    }

    [Fact]
    public async Task Scan_FindsOperaCookies_UnderRoaming_AndSensitive()
    {
        // Opera keeps Cookies/History/Sessions under Roaming AppData, not Local.
        WriteRoamingFile(@"Opera Software\Opera Stable\Network\Cookies", 512);

        var items = await _svc.ScanAsync();
        var cookies = items.FirstOrDefault(i => i.Browser == "Opera" && i.Category == "Cookies");
        Assert.NotNull(cookies);                     // was null before the fix
        Assert.True(cookies!.IsSensitive);
        Assert.False(cookies.IsSelected);            // never auto-selected
    }

    [Fact]
    public async Task Scan_FirefoxCache_TargetsCache2_NotProfileRoot()
    {
        // A Firefox profile with both a cache and sensitive files at the profile root.
        WriteFile(@"Mozilla\Firefox\Profiles\abc.default-release\cache2\entries\e0", 2048);
        WriteFile(@"Mozilla\Firefox\Profiles\abc.default-release\logins.json", 256);
        WriteFile(@"Mozilla\Firefox\Profiles\abc.default-release\key4.db", 256);

        var items = await _svc.ScanAsync();
        var ff = items.FirstOrDefault(i => i.Browser == "Firefox");
        Assert.NotNull(ff);
        // Only the cache2 bytes are counted — never the profile root's logins/keys.
        Assert.Equal(2048, ff!.SizeBytes);
        Assert.All(ff.Paths, p => Assert.EndsWith("cache2", p, StringComparison.OrdinalIgnoreCase));

        // And a clean leaves the sensitive profile files intact.
        await _svc.CleanAsync([ff]);
        Assert.True(File.Exists(Path.Combine(_local, @"Mozilla\Firefox\Profiles\abc.default-release\logins.json")));
        Assert.True(File.Exists(Path.Combine(_local, @"Mozilla\Firefox\Profiles\abc.default-release\key4.db")));
    }

    // --- Multiple Chromium profiles ------------------------------------------------
    // The profile segment used to be the literal "Default", so a second Chrome/Edge/Brave profile
    // (personal + work, or one per family member) was never scanned, sized or cleaned. Profiles are
    // now enumerated at scan time, the way Firefox's already were.

    [Fact]
    public async Task Scan_FindsCacheInEveryChromiumProfile_NotJustDefault()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 2000);
        WriteFile(@"Google\Chrome\User Data\Profile 2\Cache\data_0", 3000);

        var items = await _svc.ScanAsync();
        var caches = items.Where(i => i.Category == "Cache" && i.Browser.StartsWith("Google Chrome")).ToList();

        Assert.Equal(3, caches.Count);      // was 1 before the fix — the other 5000 bytes were invisible
        Assert.Equal(6000, caches.Sum(c => c.SizeBytes));
    }

    [Fact]
    public async Task Scan_NamesTheProfile_SoTheUserCanSeeWhichOne()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 2000);

        var items = await _svc.ScanAsync();

        // The default profile stays unlabelled so the common single-profile case reads as before…
        var dflt = items.Single(i => i.Browser == "Google Chrome" && i.Category == "Cache");
        Assert.Equal(1000, dflt.SizeBytes);
        // …and the extra profile is named, which a flat "Google Chrome" row could never convey.
        var second = items.Single(i => i.Browser == "Google Chrome — Profile 1" && i.Category == "Cache");
        Assert.Equal(2000, second.SizeBytes);
        Assert.Contains("Profile 1", second.Description);
    }

    [Fact]
    public async Task Scan_EachProfilesPaths_StayInsideThatProfile()
    {
        // The load-bearing safety property: cleaning one profile must not be able to reach another.
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 2000);

        var items = await _svc.ScanAsync();

        var dflt = items.Single(i => i.Browser == "Google Chrome" && i.Category == "Cache");
        Assert.All(dflt.Paths, p => Assert.DoesNotContain(@"\Profile 1\", p));
        var second = items.Single(i => i.Browser == "Google Chrome — Profile 1" && i.Category == "Cache");
        Assert.All(second.Paths, p => Assert.DoesNotContain(@"\Default\", p));
    }

    [Fact]
    public async Task Clean_OneProfile_LeavesTheOtherProfileUntouched()
    {
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 2000);

        var items = await _svc.ScanAsync();
        var second = items.Single(i => i.Browser == "Google Chrome — Profile 1" && i.Category == "Cache");

        await _svc.CleanAsync([second]);

        Assert.False(File.Exists(Path.Combine(_local, @"Google\Chrome\User Data\Profile 1\Cache\data_0")));
        Assert.True(File.Exists(Path.Combine(_local, @"Google\Chrome\User Data\Default\Cache\data_0")));
    }

    [Fact]
    public async Task Scan_IgnoresChromiumFoldersThatAreNotUserProfiles()
    {
        // Chromium keeps plenty of non-profile folders under User Data. Matching every subdirectory
        // would point a delete at paths this tab never advertised, so only Default / "Profile N" count.
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);
        WriteFile(@"Google\Chrome\User Data\Crashpad\Cache\data_0", 1);
        WriteFile(@"Google\Chrome\User Data\ShaderCache\Cache\data_0", 1);
        WriteFile(@"Google\Chrome\User Data\System Profile\Cache\data_0", 1);
        WriteFile(@"Google\Chrome\User Data\Guest Profile\Cache\data_0", 1);
        WriteFile(@"Google\Chrome\User Data\Profile X\Cache\data_0", 1);   // not "Profile <number>"

        var items = await _svc.ScanAsync();
        var caches = items.Where(i => i.Category == "Cache" && i.Browser.StartsWith("Google Chrome")).ToList();

        Assert.Single(caches);
        Assert.Equal(1000, caches[0].SizeBytes);
    }

    [Fact]
    public async Task Scan_MultipleProfiles_AcrossDifferentBrowsers()
    {
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 100);
        WriteFile(@"Microsoft\Edge\User Data\Profile 3\Cache\data_0", 200);
        WriteFile(@"BraveSoftware\Brave-Browser\User Data\Default\Cache\data_0", 300);

        var items = await _svc.ScanAsync();

        Assert.Contains(items, i => i.Browser == "Google Chrome — Profile 1");
        Assert.Contains(items, i => i.Browser == "Microsoft Edge — Profile 3");
        Assert.Contains(items, i => i.Browser == "Brave");   // Default stays unlabelled
    }

    [Fact]
    public async Task Scan_SkipsAProfileThatIsAJunction()
    {
        // Same guard the Firefox expander applies: a standard user can create a junction with
        // `mklink /J` and no elevation, so a profile-shaped link must not become a delete target.
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 1000);

        var victim = Path.Combine(_local, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllBytes(Path.Combine(victim, "important.dat"), new byte[4096]);

        var link = Path.Combine(_local, @"Google\Chrome\User Data\Profile 1");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryCreateJunction(link, victim)) return;   // skip where junctions are unavailable

        var items = await _svc.ScanAsync();

        Assert.DoesNotContain(items, i => i.Browser.Contains("Profile 1"));
        Assert.True(File.Exists(Path.Combine(victim, "important.dat")));
    }

    [Fact]
    public async Task Scan_ProfileOrder_IsDefaultThenNumericallyStable()
    {
        // Filesystem enumeration order is not guaranteed; the grid should read predictably.
        WriteFile(@"Google\Chrome\User Data\Profile 2\Cache\data_0", 20);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 10);
        WriteFile(@"Google\Chrome\User Data\Default\Cache\data_0", 5);

        var items = await _svc.ScanAsync();
        var order = items.Where(i => i.Category == "Cache" && i.Browser.StartsWith("Google Chrome"))
                         .Select(i => i.Browser)
                         .ToList();

        Assert.Equal(
            ["Google Chrome", "Google Chrome — Profile 1", "Google Chrome — Profile 2"],
            order);
    }

    [Fact]
    public async Task Scan_SensitiveFlags_HoldPerProfile()
    {
        // Cookies must stay opt-in in EVERY profile, not just the default one.
        WriteFile(@"Google\Chrome\User Data\Profile 1\Network\Cookies", 512);
        WriteFile(@"Google\Chrome\User Data\Profile 1\Cache\data_0", 512);

        var items = await _svc.ScanAsync();

        var cookies = items.Single(i => i.Category == "Cookies");
        Assert.True(cookies.IsSensitive);
        Assert.False(cookies.IsSelected);
        var cache = items.Single(i => i.Category == "Cache");
        Assert.True(cache.IsSelected);
    }

    // ---------- Firefox parity: Cookies + Sessions, never credentials or bookmarks (#1590) ----------
    // Firefox stores cookies/sessions in the ROAMING profile; cache is under Local. History is
    // intentionally NOT offered because places.sqlite holds bookmarks too.

    [Fact]
    public async Task Scan_FirefoxCookies_TargetsTheSqliteFiles_NeverTheProfileRoot()
    {
        // A roaming profile with cookies + the credential/bookmark files that must never be touched.
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\cookies.sqlite", 4096);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\cookies.sqlite-wal", 512);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\logins.json", 256);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\key4.db", 256);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\places.sqlite", 8192);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\prefs.js", 128);

        var items = await _svc.ScanAsync();
        var cookies = items.FirstOrDefault(i => i.Browser == "Firefox" && i.Category == "Cookies");

        Assert.NotNull(cookies);
        // Only the two cookie files that exist are sized (4096 + 512); the -shm is absent.
        Assert.Equal(4096 + 512, cookies!.SizeBytes);
        // The credential store, the key database, bookmarks/history and prefs are NEVER in the paths.
        Assert.All(cookies.Paths, p =>
        {
            Assert.DoesNotContain("logins.json", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key4.db", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("places.sqlite", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prefs.js", p, StringComparison.OrdinalIgnoreCase);
        });
        // Sensitive -> unticked by default (opt-in), like the Chromium cookie rows.
        Assert.True(cookies.IsSensitive);
        Assert.False(cookies.IsSelected);
    }

    [Fact]
    public async Task Scan_FirefoxSessions_TargetsSessionFilesOnly()
    {
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\sessionstore.jsonlz4", 2048);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\sessionstore-backups\recovery.jsonlz4", 1024);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\logins.json", 256);

        var items = await _svc.ScanAsync();
        var sessions = items.FirstOrDefault(i => i.Browser == "Firefox" && i.Category == "Sessions");

        Assert.NotNull(sessions);
        Assert.Equal(2048 + 1024, sessions!.SizeBytes);
        Assert.All(sessions.Paths, p => Assert.DoesNotContain("logins.json", p, StringComparison.OrdinalIgnoreCase));
        Assert.True(sessions.IsSensitive);
    }

    [Fact]
    public async Task Scan_Firefox_NeverOffersHistory_BecausePlacesSqliteHoldsBookmarks()
    {
        // places.sqlite present, but there must be no History row for Firefox — deleting it would
        // drop the user's bookmarks along with history.
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\places.sqlite", 8192);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\cookies.sqlite", 1024);

        var items = await _svc.ScanAsync();

        Assert.DoesNotContain(items, i => i.Browser == "Firefox" && i.Category == "History");
    }

    [Fact]
    public async Task Clean_FirefoxCookies_RemovesCookiesButLeavesCredentialsAndBookmarks()
    {
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\cookies.sqlite", 4096);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\logins.json", 256);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\key4.db", 256);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\abc.default-release\places.sqlite", 8192);

        var items = await _svc.ScanAsync();
        var cookies = items.First(i => i.Browser == "Firefox" && i.Category == "Cookies");
        await _svc.CleanAsync([cookies]);

        var profile = Path.Combine(_roaming, @"Mozilla\Firefox\Profiles\abc.default-release");
        Assert.False(File.Exists(Path.Combine(profile, "cookies.sqlite")));   // cleaned
        Assert.True(File.Exists(Path.Combine(profile, "logins.json")));       // saved logins untouched
        Assert.True(File.Exists(Path.Combine(profile, "key4.db")));           // key database untouched
        Assert.True(File.Exists(Path.Combine(profile, "places.sqlite")));     // history + bookmarks untouched
    }

    [Fact]
    public async Task Scan_FirefoxMultipleProfiles_EachGetsCookiesAndSessions()
    {
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\aaa.default-release\cookies.sqlite", 1024);
        WriteRoamingFile(@"Mozilla\Firefox\Profiles\bbb.dev-edition\cookies.sqlite", 2048);

        var items = await _svc.ScanAsync();
        var cookieRows = items.Where(i => i.Browser == "Firefox" && i.Category == "Cookies").ToList();

        Assert.Equal(2, cookieRows.Count);   // one per profile, like the Chromium per-profile expansion
    }
}
