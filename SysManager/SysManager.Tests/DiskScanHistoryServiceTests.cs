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

    private string HistoryFile => Path.Combine(_dir, "disk-scan-history.json");

    // The history as a reader sees it once the file could be read. An unreadable history is its own answer, and
    // only the tests about that ask for it (#2521).
    private static async Task<IReadOnlyList<DiskScanSnapshot>> LoadedAsync(DiskScanHistoryService svc)
    {
        var all = await svc.LoadAsync();
        Assert.NotNull(all);
        return all;
    }

    private static async Task<DiskScanSnapshot?> FoundAsync(DiskScanHistoryService svc, string root)
    {
        var (readable, snapshot) = await svc.FindAsync(root);
        Assert.True(readable, "the history could not be read");
        return snapshot;
    }

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
        Assert.Null(await FoundAsync(svc, @"C:\Data"));
    }

    [Fact]
    public async Task Save_ThenFind_RoundTripsTheSnapshot()
    {
        using var svc = NewService();
        var at = new DateTime(2026, 7, 12, 9, 0, 0);

        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 5_000, at, ("Sub", 4_000))));

        var found = await FoundAsync(svc, @"C:\Data");
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

        var all = await LoadedAsync(svc);
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

        Assert.Single(await LoadedAsync(svc));
        Assert.Equal(2, (await FoundAsync(svc, first))!.TotalSize);
    }

    [Fact]
    public async Task Save_BoundsRootsToTheCap_DroppingTheOldest()
    {
        using var svc = NewService();
        // One more than the cap; the oldest by capture time must fall off.
        for (var i = 0; i <= DiskScanHistoryService.MaxRoots; i++)
            await svc.SaveAsync(Snap($@"C:\Root{i}", i, new DateTime(2026, 1, 1).AddDays(i)));

        var all = await LoadedAsync(svc);
        Assert.Equal(DiskScanHistoryService.MaxRoots, all.Count);
        Assert.Null(await FoundAsync(svc, @"C:\Root0"));                    // oldest dropped
        Assert.NotNull(await FoundAsync(svc, $@"C:\Root{DiskScanHistoryService.MaxRoots}")); // newest kept
    }

    [Fact]
    public async Task Save_BoundsFoldersPerRoot_KeepingTheLargest()
    {
        using var svc = NewService();
        var folders = Enumerable.Range(1, DiskScanHistoryService.MaxFoldersPerRoot + 5)
            .Select(i => ($"F{i}", (long)i * 100))
            .ToArray();
        await svc.SaveAsync(Snap(@"C:\Data", 9_999, new DateTime(2026, 1, 1), folders));

        var found = await FoundAsync(svc, @"C:\Data");
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

        Assert.Empty(await LoadedAsync(svc));
        Assert.Null(await FoundAsync(svc, @"C:\Data"));
        // And a save over the corrupt file recovers rather than throwing.
        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));
        Assert.Single(await LoadedAsync(svc));
    }

    // ── A history that is there but cannot be read or parsed (#2521) ──────────────────────────────
    //
    // A history that could not be read loaded as empty, so the next save wrote a history holding only that scan
    // over every other folder's. The file is held open with delete sharing only for as long as a read must fail:
    // the read fails, and the replace a write ends with would still succeed.

    private FileStream HoldAgainstReads() => new(HistoryFile, FileMode.Open, FileAccess.Read, FileShare.Delete);

    [Fact]
    public async Task Load_WhenTheFileCannotBeRead_IsNull_NotEmpty()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));

        using (HoldAgainstReads())
            Assert.Null(await svc.LoadAsync());

        Assert.Single(await LoadedAsync(svc));   // readable again once the file is released
    }

    [Fact]
    public async Task Find_WhenTheFileCannotBeRead_SaysSo_RatherThanNeverScanned()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));

        using (HoldAgainstReads())
        {
            var (readable, snapshot) = await svc.FindAsync(@"C:\Data");
            Assert.False(readable);
            Assert.Null(snapshot);
        }
    }

    [Fact]
    public async Task Save_WhenTheHistoryCannotBeRead_WritesNothing_AndSaysSo()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Snap(@"C:\First", 1, new DateTime(2026, 1, 1))));
        Assert.True(await svc.SaveAsync(Snap(@"C:\Second", 2, new DateTime(2026, 1, 2))));

        using (HoldAgainstReads())
            Assert.False(await svc.SaveAsync(Snap(@"C:\Third", 3, new DateTime(2026, 1, 3))));

        Assert.Equal(2, (await LoadedAsync(svc)).Count);
        Assert.Null(await FoundAsync(svc, @"C:\Third"));
    }

    [Fact]
    public async Task Save_OverAHistoryThatDoesNotParse_KeepsItAside_AndSaves()
    {
        File.WriteAllText(HistoryFile, "{ this is not valid json ]");
        using var svc = NewService();

        Assert.True(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));

        Assert.Equal("{ this is not valid json ]", File.ReadAllText(HistoryFile + ".unreadable"));
        Assert.Single(await LoadedAsync(svc));
    }

    [Fact]
    public async Task Save_WhenAHistoryThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(HistoryFile, "{ this is not valid json ]");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(HistoryFile + ".unreadable");
        using var svc = NewService();

        Assert.False(await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1))));

        Assert.Equal("{ this is not valid json ]", File.ReadAllText(HistoryFile));
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        using var svc = NewService();
        await svc.SaveAsync(Snap(@"C:\Data", 1, new DateTime(2026, 1, 1)));

        Assert.True(await svc.ClearAsync());
        Assert.Empty(await LoadedAsync(svc));
        Assert.True(await svc.ClearAsync());   // idempotent — clearing an already-empty history is fine
    }
}
