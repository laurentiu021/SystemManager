// SysManager · ExplorerShellTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for the half of <see cref="ExplorerShell"/> that can be tested: the cache sweep, against a
/// temp folder.
/// </summary>
/// <remarks>
/// <see cref="ExplorerShell.Stop"/>/<see cref="ExplorerShell.Start"/> are deliberately NOT exercised —
/// they end and relaunch the real desktop, which no test may do. Splitting the sweep out behind a
/// directory parameter is what makes the part with actual logic (which patterns, what is counted,
/// what happens to a locked file) provable without touching the shell.
/// </remarks>
public class ExplorerShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sysmanager-explorershell-" + Guid.NewGuid().ToString("N"));

    public ExplorerShellTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a locked file from the sharing test — the temp folder is disposable */ }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, int bytes = 16)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void CacheFilePatterns_CoverBothCacheFamilies()
    {
        Assert.Contains("thumbcache_*.db", ExplorerShell.CacheFilePatterns);
        Assert.Contains("iconcache_*.db", ExplorerShell.CacheFilePatterns);
    }

    /// <summary>A property, so a caller cannot mutate the list every other caller relies on.</summary>
    [Fact]
    public void CacheFilePatterns_HandsOutACopy()
    {
        var first = ExplorerShell.CacheFilePatterns;
        first[0] = "mutated";

        Assert.DoesNotContain("mutated", ExplorerShell.CacheFilePatterns);
    }

    [Fact]
    public void DeleteCacheFiles_RemovesBothFamiliesAndCountsTheBytes()
    {
        Write("thumbcache_256.db", 100);
        Write("iconcache_32.db", 50);

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(2, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(150, sweep.BytesFreed);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// The load-bearing negative: Explorer's folder holds state Windows does NOT rebuild, so anything
    /// outside the two patterns must survive.
    /// </summary>
    [Fact]
    public void DeleteCacheFiles_LeavesEverythingElseAlone()
    {
        Write("thumbcache_256.db");
        var keep = new[]
        {
            Write("iconcache.db"),            // no underscore-suffix — not the pattern
            Write("thumbcache.db"),
            Write("startupInfo.bin"),
            Write("notacache.txt"),
            Write("thumbcache_256.db.bak"),   // pattern must anchor at the END
        };

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(1, sweep.Deleted);
        foreach (var path in keep)
        {
            Assert.True(File.Exists(path), $"{Path.GetFileName(path)} was deleted and should not have been.");
        }
    }

    /// <summary>
    /// A file another process holds open is counted as FAILED, never as freed — the whole point of the
    /// operation is that Explorer locks these, and reporting a byte it did not reclaim would be the one
    /// lie a repair tool must not tell.
    /// </summary>
    [Fact]
    public void DeleteCacheFiles_CountsALockedFileAsFailedNotFreed()
    {
        Write("thumbcache_free.db", 100);
        var locked = Write("thumbcache_locked.db", 999);

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var sweep = ExplorerShell.DeleteCacheFiles(_dir);

            Assert.Equal(1, sweep.Deleted);
            Assert.Equal(1, sweep.Failed);
            Assert.Equal(100, sweep.BytesFreed);   // the locked file's 999 bytes are NOT counted
        }

        Assert.True(File.Exists(locked), "the locked file should still be there");
    }

    /// <summary>Read-only is cleared first — <c>File.Delete</c> refuses one, and Windows marks some.</summary>
    [Fact]
    public void DeleteCacheFiles_RemovesAReadOnlyCacheFile()
    {
        var path = Write("iconcache_16.db", 40);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(1, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteCacheFiles_OnAnEmptyFolder_ReportsNothing()
    {
        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(0, sweep.BytesFreed);
    }

    /// <summary>A missing folder is not an error — it means Windows has already rebuilt everything.</summary>
    [Fact]
    public void DeleteCacheFiles_OnAMissingFolder_ReturnsEmptyRatherThanThrowing()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        var sweep = ExplorerShell.DeleteCacheFiles(missing);

        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
    }

    [Fact]
    public void CacheDirectory_IsExplorersOwnFolderUnderTheUsersProfile()
    {
        var dir = ExplorerShell.CacheDirectory;

        Assert.EndsWith(Path.Combine("Microsoft", "Windows", "Explorer"), dir, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            dir,
            StringComparison.Ordinal);
    }
}
