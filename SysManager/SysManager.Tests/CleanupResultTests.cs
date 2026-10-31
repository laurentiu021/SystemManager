// SysManager · CleanupResultTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="CleanupResult"/> — summary model returned by DeepCleanupService.
/// </summary>
public class CleanupResultTests
{
    [Fact]
    public void Summary_NoErrors_ShowsCleanMessage()
    {
        var r = new CleanupResult { BytesFreed = 1024 * 1024 * 500L, FilesDeleted = 42 };
        Assert.Contains("500", r.Summary);
        Assert.Contains("42", r.Summary);
        Assert.DoesNotContain("skipped", r.Summary);
    }

    [Fact]
    public void Summary_WithErrors_ShowsSkippedCount()
    {
        var r = new CleanupResult
        {
            BytesFreed = 1024 * 1024,
            FilesDeleted = 10,
            Errors = new[] { "file1: access denied", "file2: in use" }
        };
        Assert.Contains("2 skipped", r.Summary);
    }

    [Fact]
    public void Summary_ZeroBytes_ShowsZero()
    {
        var r = new CleanupResult { BytesFreed = 0, FilesDeleted = 0 };
        Assert.Contains("0 B", r.Summary);
        Assert.Contains("0", r.Summary);
    }

    [Fact]
    public void Defaults_AreZero()
    {
        var r = new CleanupResult();
        Assert.Equal(0, r.BytesFreed);
        Assert.Equal(0, r.FilesDeleted);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Summary_LargeValues_FormatsCorrectly()
    {
        var r = new CleanupResult
        {
            BytesFreed = 5L * 1024 * 1024 * 1024, // 5 GB
            FilesDeleted = 1500
        };
        Assert.Contains("5", r.Summary);
        Assert.Contains("GB", r.Summary);
        Assert.Contains("1,500", r.Summary);
    }

    // ---------- a cancelled clean (#2736) ----------
    // The summary is what the tab shows and what Recent activity keeps, so a cancelled clean says it stopped, with
    // what it had already removed.

    private static readonly RecycleBinContents FourItems = new(2L << 20, 4);

    [Fact]
    public void Summary_OfACancelledClean_SaysItStopped_AndWhatItFreed()
    {
        var r = new CleanupResult { BytesFreed = 3L << 20, FilesDeleted = 1204, Cancelled = true };
        Assert.Equal("Cancelled after it freed 3.0 MB across 1,204 files", r.Summary);
    }

    [Fact]
    public void Summary_OfACancelledClean_NamesTheRecycleBin_AndWhatWasSkipped()
    {
        var r = new CleanupResult
        {
            BytesFreed = 5L << 20,
            FilesDeleted = 3,
            RecycleBinEmptied = FourItems,
            Errors = ["a.tmp: in use"],
            Cancelled = true,
        };
        Assert.Equal("Cancelled after it freed 5.0 MB across 3 files, and emptied the Recycle Bin (4 items, 2.0 MB) "
            + "· 1 skipped", r.Summary);
    }

    [Fact]
    public void Summary_OfACleanCancelledBeforeAnythingWent_SaysSo()
        => Assert.Equal("Cancelled before anything was removed", new CleanupResult { Cancelled = true }.Summary);

    [Fact]
    public void Summary_OfAFinishedClean_ReadsAsBefore()
    {
        Assert.Equal("Freed 3.0 MB across 1,204 files",
            new CleanupResult { BytesFreed = 3L << 20, FilesDeleted = 1204 }.Summary);
        Assert.Equal("Freed 2.0 MB by emptying the Recycle Bin (4 items, 2.0 MB)",
            new CleanupResult { BytesFreed = 2L << 20, RecycleBinEmptied = FourItems }.Summary);
        Assert.Equal("Freed 0 B across 0 files", new CleanupResult().Summary);
    }

    [Fact]
    public void RemovedAnything_CountsFiles_Bytes_AndTheRecycleBin()
    {
        Assert.False(new CleanupResult().RemovedAnything);
        Assert.True(new CleanupResult { FilesDeleted = 1 }.RemovedAnything);
        Assert.True(new CleanupResult { BytesFreed = 1 }.RemovedAnything);
        Assert.True(new CleanupResult { RecycleBinEmptied = FourItems }.RemovedAnything);
    }
}
