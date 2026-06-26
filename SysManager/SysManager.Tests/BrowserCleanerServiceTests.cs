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
        // The junctioned cache must not contribute the victim's bytes/files to the scan.
        if (cache is not null)
        {
            Assert.Equal(0, cache.SizeBytes);
            Assert.Equal(0, cache.FileCount);
        }
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
}
