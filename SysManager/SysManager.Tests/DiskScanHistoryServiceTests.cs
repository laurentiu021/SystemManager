// SysManager · DiskScanHistoryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskScanHistoryService"/> — the per-root persistence behind the Disk Analyzer's
/// "since last scan" line. Everything runs against a temp directory via the <c>configDir</c> seam, so the
/// real save/load/upsert/trim paths are exercised without touching the user's own history file.
/// </summary>
public sealed class DiskScanHistoryServiceTests : IDisposable
{
    private readonly string _dir;

    public DiskScanHistoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerDiskHist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DiskScanHistoryService NewService() => new(_dir);

    private static DiskScanSnapshot Snap(string root, long total, DateTime at, params (string Name, long Size)[] folders)
        => new()
        {
            RootPath = root,
            TotalSize = total,
            CapturedAt = at,
            TopFolders = folders.Select(f => new FolderUsage { Name = f.Name, SizeBytes = f.Size }).ToList(),
        };

    [Fact]
    public async Task Find_BeforeAnyScan_IsNull()
    {
        using var svc = NewService();
        Assert.Null(await svc.FindAsync(@"C:\Data"));
    }

    [Fact]
    public async Task Save_ThenFind_RoundTripsTheSnapshot()
    {
        using var svc = NewService();
        var at = new DateTime(2026, 7, 12, 9, 0, 0);

        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 5_000, at, ("Sub", 4_000))));

        var found = await svc.FindAsync(@"C:\Data");
        Assert.NotNull(found);
        Assert.Equal(5_000, found!.TotalSize);
        Assert.Equal(at, found.CapturedAt);
        Assert.Equal("Sub", Assert.Single(found.TopFolders).Name);
    }

    [Fact]
    public async Task Save_SameRootTwice_KeepsOnlyTheNewer()
    {
        using var svc = NewService();
        await svc.SaveAsync(Snap(@"C:\Data", 100, new DateTime(2026, 1, 1)));
        await svc.SaveAsync(Snap(@"C:\Data", 999, new DateTime(2026, 2, 1)));

        var all = await svc.LoadAsync();
        Assert.Single(all);
        Assert.Equal(999, all[0].TotalSize);   // the upsert replaced, it did not accumulate
    }

    [Theory]
    [InlineData(@"C:\Data", @"C:\Data\")]      // trailing separator
    [InlineData(@"C:\Data", @"c:\data")]        // case
    public async Task Save_TreatsEquivalentPathsAsOneRoot(string first, string second)
    {
        using var svc = NewService();
        await svc.SaveAsync(Snap(first, 1, new DateTime(2026, 1, 1)));
        await svc.SaveAsync(Snap(second, 2, new DateTime(2026, 2, 1)));

        Assert.Single(await svc.LoadAsync());
        Assert.Equal(2, (await svc.FindAsync(first))!.TotalSize);
    }

    [Fact]
    public async Task Save_BoundsRootsToTheCap_DroppingTheOldest()
    {
        using var svc = NewService();
        // One more than the cap; the oldest by capture time must fall off.
        for (var i = 0; i <= DiskScanHistoryService.MaxRoots; i++)
            await svc.SaveAsync(Snap($@"C:\Root{i}", i, new DateTime(2026, 1, 1).AddDays(i)));

        var all = await svc.LoadAsync();
        Assert.Equal(DiskScanHistoryService.MaxRoots, all.Count);
        Assert.Null(await svc.FindAsync(@"C:\Root0"));                    // oldest dropped
        Assert.NotNull(await svc.FindAsync($@"C:\Root{DiskScanHistoryService.MaxRoots}")); // newest kept
    }

    [Fact]
    public async Task Save_BoundsFoldersPerRoot_KeepingTheLargest()
    {
        using var svc = NewService();
        var folders = Enumerable.Range(1, DiskScanHistoryService.MaxFoldersPerRoot + 5)
            .Select(i => ($"F{i}", (long)i * 100))
            .ToArray();
        await svc.SaveAsync(Snap(@"C:\Data", 9_999, new DateTime(2026, 1, 1), folders));

        var found = await svc.FindAsync(@"C:\Data");
        Assert.Equal(DiskScanHistoryService.MaxFoldersPerRoot, found!.TopFolders.Count);
        // Largest kept, smallest dropped.
        Assert.Contains(found.TopFolders, f => f.Name == $"F{DiskScanHistoryService.MaxFoldersPerRoot + 5}");
        Assert.DoesNotContain(found.TopFolders, f => f.Name == "F1");
    }

    [Fact]
    public async Task Load_OnCorruptFile_DegradesToEmpty_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_dir, "disk-scan-history.json"), "{ this is not valid json ]");
        using var svc = NewService();

        Assert.Empty(await svc.LoadAsync());
        Assert.Null(await svc.FindAsync(@"C:\Data"));
        // And a save over the corrupt file recovers rather than throwing.
        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));
        Assert.Single(await svc.LoadAsync());
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        using var svc = NewService();
        await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1)));

        Assert.True(await svc.ClearAsync());
        Assert.Empty(await svc.LoadAsync());
        Assert.True(await svc.ClearAsync());   // idempotent — clearing an already-empty history is fine
    }
}
