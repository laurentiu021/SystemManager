// SysManager · DeepCleanupFilteredBucketTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The two buckets that must count and delete only SOME of the files under their path — blue-screen
/// memory dumps and the Explorer thumbnail cache (#1577).
/// </summary>
/// <remarks>
/// Every other bucket owns its whole folder, so "everything under this path" was a correct rule for all
/// nineteen of them. These two do not: <c>%LOCALAPPDATA%\Microsoft\Windows\Explorer</c> is Explorer's own
/// working folder and only the <c>thumbcache_*.db</c> / <c>iconcache_*.db</c> files in it are rebuildable
/// caches, while the dumps bucket names one exact file (<c>MEMORY.DMP</c>) alongside two folders. So the
/// filter is not a convenience — without it this feature deletes files it never claimed to.
/// <para><b>Scan and Clean are asserted separately and both must filter.</b> They are two code paths over
/// the same definitions: Scan aggregates and Clean deletes, and Clean re-reads the filter off the
/// <see cref="CleanupCategory"/> rather than from the definition it came from. A filter applied in Scan
/// only would produce the worst possible outcome — an honest size on screen, and a delete that took the
/// whole folder.</para>
/// </remarks>
public sealed class DeepCleanupFilteredBucketTests
{
    private static TempCleanupRoots Roots() => new();

    private static string WriteFile(string path, int bytes) => TempCleanupRoots.WriteFile(path, bytes);

    private static CleanupCategory Named(IReadOnlyList<CleanupCategory> categories, string name)
    {
        var found = categories.FirstOrDefault(c => c.Name == name);
        Assert.NotNull(found);   // renamed or removed — fix this test rather than letting it assert nothing
        return found!;
    }

    private static string ExplorerCache(TempCleanupRoots roots)
        => Path.Combine(roots.LocalAppData, "Microsoft", "Windows", "Explorer");

    // ── Blue-screen memory dumps ─────────────────────────────────────────────

    /// <summary>
    /// <c>MEMORY.DMP</c> is a FILE, not a folder — every other bucket names directories, and the scan
    /// only looked at paths <c>Directory.Exists</c> agreed with, so naming it would have been silently
    /// dropped.
    /// </summary>
    [Fact]
    public async Task Scan_CountsTheMemoryDumpFileItself()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.WindowsDirectory, "MEMORY.DMP"), 40);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Blue-screen memory dumps");
        Assert.Equal(1, category.FileCount);
        Assert.Equal(40, category.TotalSizeBytes);
    }

    /// <summary>The two dump folders are walked, and <c>LiveKernelReports</c> keeps its dumps in subfolders.</summary>
    [Fact]
    public async Task Scan_CountsMinidumpsAndLiveKernelReportsBelowTheirFolders()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.WindowsDirectory, "Minidump", "010125-1234.dmp"), 10);
        WriteFile(Path.Combine(roots.WindowsDirectory, "LiveKernelReports", "WATCHDOG", "a.dmp"), 20);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Blue-screen memory dumps");
        Assert.Equal(2, category.FileCount);
        Assert.Equal(30, category.TotalSizeBytes);
    }

    /// <summary>
    /// Deleting a dump ends any investigation into why the machine crashed, so this bucket follows the
    /// <c>Windows.old</c> precedent: offered, described, never ticked for you.
    /// </summary>
    [Fact]
    public async Task Scan_NeverPreSelectsTheDumpsBucket_EvenWhenItHasContent()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.WindowsDirectory, "MEMORY.DMP"), 4096);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Blue-screen memory dumps");
        Assert.True(category.IsDestructiveHint);
        Assert.False(category.IsSelected);
        Assert.Equal(4096, category.TotalSizeBytes);
    }

    /// <summary>
    /// <c>LiveKernelReports</c> also holds <c>.etl</c> traces and XML metadata. The bucket is named for
    /// dumps and its size is what the user reads before ticking it, so anything else must not be counted.
    /// </summary>
    [Fact]
    public async Task Scan_IgnoresNonDumpFilesInTheDumpFolders()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.WindowsDirectory, "LiveKernelReports", "a.dmp"), 10);
        WriteFile(Path.Combine(roots.WindowsDirectory, "LiveKernelReports", "trace.etl"), 500);
        WriteFile(Path.Combine(roots.WindowsDirectory, "Minidump", "notes.txt"), 700);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Blue-screen memory dumps");
        Assert.Equal(1, category.FileCount);
        Assert.Equal(10, category.TotalSizeBytes);
    }

    // ── Explorer thumbnail & icon cache ──────────────────────────────────────

    [Fact]
    public async Task Scan_CountsThumbnailAndIconCacheFiles()
    {
        using var roots = Roots();
        var explorer = ExplorerCache(roots);
        WriteFile(Path.Combine(explorer, "thumbcache_256.db"), 10);
        WriteFile(Path.Combine(explorer, "thumbcache_idx.db"), 20);
        WriteFile(Path.Combine(explorer, "iconcache_16.db"), 30);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Explorer thumbnail & icon cache");
        Assert.Equal(3, category.FileCount);
        Assert.Equal(60, category.TotalSizeBytes);
    }

    /// <summary>
    /// The Explorer folder is not a cache folder — it is where Explorer keeps its own state, including
    /// the jump lists that hold the user's recent-files history. Counting the whole folder would put that
    /// history inside a bucket described as rebuildable, and then delete it.
    /// </summary>
    [Fact]
    public async Task Scan_IgnoresEverythingElseInTheExplorerFolder()
    {
        using var roots = Roots();
        var explorer = ExplorerCache(roots);
        WriteFile(Path.Combine(explorer, "thumbcache_256.db"), 10);
        WriteFile(Path.Combine(explorer, "AutomaticDestinations", "recent.automaticDestinations-ms"), 900);
        WriteFile(Path.Combine(explorer, "notifications.db"), 500);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Explorer thumbnail & icon cache");
        Assert.Equal(1, category.FileCount);
        Assert.Equal(10, category.TotalSizeBytes);
    }

    /// <summary>Windows rebuilds these on demand, so unlike the dumps this one may arrive ticked.</summary>
    [Fact]
    public async Task Scan_PreSelectsTheThumbnailCache()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(ExplorerCache(roots), "thumbcache_1024.db"), 10);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Explorer thumbnail & icon cache");
        Assert.False(category.IsDestructiveHint);
        Assert.True(category.IsSelected);
    }

    // ── Clean applies the same filter ────────────────────────────────────────

    /// <summary>
    /// The one that matters. Clean walks <see cref="CleanupCategory.Paths"/> itself; if the filter lives
    /// only in the scan definitions, the user sees "1 file · 10 bytes" and loses their jump lists.
    /// </summary>
    [Fact]
    public async Task Clean_DeletesOnlyTheFilesThatMatchTheFilter()
    {
        using var roots = Roots();
        var explorer = ExplorerCache(roots);
        var cache = WriteFile(Path.Combine(explorer, "thumbcache_256.db"), 10);
        var jumpList = WriteFile(Path.Combine(explorer, "AutomaticDestinations", "recent.automaticDestinations-ms"), 900);
        var other = WriteFile(Path.Combine(explorer, "notifications.db"), 500);

        var service = new DeepCleanupService(roots);
        var categories = await service.ScanAsync();
        var category = Named(categories, "Explorer thumbnail & icon cache");
        category.IsSelected = true;

        var result = await service.CleanAsync([category]);

        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(jumpList), "Clean deleted Explorer's recent-files history, which this bucket never counted.");
        Assert.True(File.Exists(other));
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(10, result.BytesFreed);
    }

    /// <summary>
    /// A filtered bucket owns files, not folders. Deleting the emptied folder would remove a directory
    /// Explorer expects to exist and that the bucket's own scan never counted.
    /// </summary>
    [Fact]
    public async Task Clean_LeavesTheFoldersOfAFilteredBucketInPlace()
    {
        using var roots = Roots();
        var explorer = ExplorerCache(roots);
        WriteFile(Path.Combine(explorer, "thumbcache_256.db"), 10);
        var staging = Path.Combine(explorer, "ThumbCacheToDelete");
        Directory.CreateDirectory(staging);

        var service = new DeepCleanupService(roots);
        var categories = await service.ScanAsync();
        var category = Named(categories, "Explorer thumbnail & icon cache");
        category.IsSelected = true;

        await service.CleanAsync([category]);

        Assert.True(Directory.Exists(staging), "Clean removed a folder Explorer owns.");
        Assert.True(Directory.Exists(explorer));
    }

    /// <summary>Clean has to handle a path that is a file, for the same reason Scan does.</summary>
    [Fact]
    public async Task Clean_DeletesTheMemoryDumpFileItself()
    {
        using var roots = Roots();
        var dump = WriteFile(Path.Combine(roots.WindowsDirectory, "MEMORY.DMP"), 40);
        var minidump = WriteFile(Path.Combine(roots.WindowsDirectory, "Minidump", "010125-1234.dmp"), 10);

        var service = new DeepCleanupService(roots);
        var categories = await service.ScanAsync();
        var category = Named(categories, "Blue-screen memory dumps");
        category.IsSelected = true;

        var result = await service.CleanAsync([category]);

        Assert.False(File.Exists(dump));
        Assert.False(File.Exists(minidump));
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(50, result.BytesFreed);
    }

    /// <summary>
    /// An unfiltered bucket must keep deleting its emptied subfolders — the filter is opt-in per bucket,
    /// and this is the behaviour every other one of them relies on.
    /// </summary>
    [Fact]
    public async Task Clean_StillRemovesEmptiedFoldersForAnUnfilteredBucket()
    {
        using var roots = Roots();
        var amd = Path.Combine(roots.SystemDrive, "AMD");
        WriteFile(Path.Combine(amd, "nested", "a.bin"), 10);

        var service = new DeepCleanupService(roots);
        var categories = await service.ScanAsync();
        var category = Named(categories, "AMD installer leftovers");
        category.IsSelected = true;

        await service.CleanAsync([category]);

        Assert.False(Directory.Exists(Path.Combine(amd, "nested")));
    }
}
