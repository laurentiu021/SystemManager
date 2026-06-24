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
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
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
}
