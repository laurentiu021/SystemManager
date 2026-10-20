// SysManager · DeepCleanupRecycleBinTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Deep Cleanup sizes the Recycle Bin as the shell counts it, empties it through the shell, and names it in the result
/// that goes to Recent activity.
/// </summary>
/// <remarks>
/// The bin is a substitute, so no test here measures or empties the Recycle Bin of the PC running it. The scan walked
/// the bin's folders, which counted the shell's own bookkeeping with the contents: the <c>desktop.ini</c> every bin
/// folder holds and the <c>$I</c> index file beside each deleted item. So an empty bin read as one file of 129 bytes
/// and arrived ticked, and a bin of N files read as 2N + 1.
/// </remarks>
public sealed class DeepCleanupRecycleBinTests
{
    private const string BucketName = "Recycle Bin (all drives)";

    private static IRecycleBin Bin(RecycleBinContents? holds, RecycleBinEmptyResult? empties = null)
    {
        var bin = Substitute.For<IRecycleBin>();
        bin.Query().Returns(holds);
        if (empties is { } result) bin.Empty().Returns(result);
        return bin;
    }

    private static CleanupCategory Named(IReadOnlyList<CleanupCategory> categories, string name)
    {
        var found = categories.FirstOrDefault(c => c.Name == name);
        Assert.NotNull(found);   // renamed or removed — fix this test rather than letting it assert nothing
        return found!;
    }

    /// <summary>The categories the scan found, with only the Recycle Bin and <paramref name="others"/> ticked.</summary>
    private static async Task<IReadOnlyList<CleanupCategory>> ScanAndTickAsync(DeepCleanupService service,
                                                                               params string[] others)
    {
        var categories = await service.ScanAsync();
        foreach (var category in categories) category.IsSelected = category.Name == BucketName || others.Contains(category.Name);
        Assert.Equal(1 + others.Length, categories.Count(c => c.IsSelected));   // a renamed bucket must fail loudly here
        return categories;
    }

    // ── The scan ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_SizesTheBinAsTheShellCountsIt_InItems()
    {
        using var roots = new TempCleanupRoots();

        var bin = Named(await new DeepCleanupService(roots, recycleBin: Bin(new RecycleBinContents(3000, 3))).ScanAsync(),
                        BucketName);

        Assert.Equal(3000, bin.TotalSizeBytes);
        Assert.Equal(3, bin.FileCount);
        Assert.Equal("3 items", bin.CountDisplay);
        Assert.True(bin.IsSelected);
    }

    [Fact]
    public async Task Scan_OfAnEmptyBin_ReportsNothing_AndLeavesItUnticked_WhateverItsFoldersHold()
    {
        // What an empty bin's folder holds: the shell's own files, which the old walk counted as one to delete.
        using var roots = new TempCleanupRoots();
        var folder = Path.Combine(roots.Root, "RecycleBin", "S-1-5-21-1000");
        TempCleanupRoots.WriteFile(Path.Combine(folder, "desktop.ini"), 129);
        TempCleanupRoots.WriteFile(Path.Combine(folder, "$IABC123.txt"), 100);
        roots.RecycleBinPaths = [folder];

        var bin = Named(await new DeepCleanupService(roots, recycleBin: Bin(new RecycleBinContents(0, 0))).ScanAsync(),
                        BucketName);

        Assert.Equal(0, bin.TotalSizeBytes);
        Assert.Equal(0, bin.FileCount);
        Assert.Equal(0, bin.SkippedCount);
        Assert.False(bin.IsSelected);
    }

    [Fact]
    public async Task Scan_OfABinTheShellCannotRead_CountsItSkipped_RatherThanEmpty()
    {
        using var roots = new TempCleanupRoots();

        var bin = Named(await new DeepCleanupService(roots, recycleBin: Bin(holds: null)).ScanAsync(), BucketName);

        Assert.Equal(1, bin.SkippedCount);
        Assert.Equal("0 items · 1 skipped", bin.CountDisplay);
        Assert.False(bin.IsSelected);
    }

    [Fact]
    public async Task Scan_WithoutABin_NeitherAsksNorWalksItsFolders()
    {
        using var roots = new TempCleanupRoots();
        var folder = Path.Combine(roots.Root, "RecycleBin", "S-1-5-21-1000");
        TempCleanupRoots.WriteFile(Path.Combine(folder, "$RABC123.txt"), 500);
        roots.RecycleBinPaths = [folder];

        var bin = Named(await new DeepCleanupService(roots).ScanAsync(), BucketName);

        Assert.Equal(0, bin.TotalSizeBytes);
        Assert.Equal(0, bin.FileCount);
        Assert.Equal(0, bin.SkippedCount);
        Assert.False(bin.IsSelected);
    }

    // ── The clean ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clean_EmptiesTheBinThroughTheShell_AndNamesItInTheSummary()
    {
        // What went is what the shell said the bin held just before emptying it, which is newer than the scan.
        using var roots = new TempCleanupRoots();
        var shell = Bin(new RecycleBinContents(3000, 3), new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(4096, 4)));
        var service = new DeepCleanupService(roots, recycleBin: shell);

        var result = await service.CleanAsync(await ScanAndTickAsync(service));

        shell.Received(1).Empty();
        Assert.Equal(4096, result.BytesFreed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(new RecycleBinContents(4096, 4), result.RecycleBinEmptied);
        Assert.Empty(result.Errors);
        Assert.Equal("Freed 4.0 KB by emptying the Recycle Bin (4 items, 4.0 KB)", result.Summary);
    }

    [Fact]
    public async Task Clean_OfFilesAndTheBin_NamesBoth()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(roots.SystemDrive, "AMD", "a.bin"), 1024);
        TempCleanupRoots.WriteFile(Path.Combine(roots.SystemDrive, "AMD", "b.bin"), 1024);
        var shell = Bin(new RecycleBinContents(3000, 3), new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(3000, 3)));
        var service = new DeepCleanupService(roots, recycleBin: shell);

        var result = await service.CleanAsync(await ScanAndTickAsync(service, "AMD installer leftovers"));

        Assert.Equal(2048 + 3000, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal("Freed 4.9 KB across 2 files, and emptied the Recycle Bin (3 items, 2.9 KB)", result.Summary);
    }

    [Fact]
    public async Task Clean_OfABinEmptiedSinceTheScan_FreesNothing_AndDoesNotNameIt()
    {
        using var roots = new TempCleanupRoots();
        var shell = Bin(new RecycleBinContents(3000, 3), new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(0, 0)));
        var service = new DeepCleanupService(roots, recycleBin: shell);

        var result = await service.CleanAsync(await ScanAndTickAsync(service));

        Assert.Equal(0, result.BytesFreed);
        Assert.Null(result.RecycleBinEmptied);
        Assert.Empty(result.Errors);
        Assert.Equal("Freed 0 B across 0 files", result.Summary);
    }

    [Fact]
    public async Task Clean_WhenWindowsRefuses_SaysSo_AndFreesNothing()
    {
        using var roots = new TempCleanupRoots();
        var shell = Bin(new RecycleBinContents(3000, 3), new RecycleBinEmptyResult(Emptied: false, new RecycleBinContents(3000, 3)));
        var service = new DeepCleanupService(roots, recycleBin: shell);

        var result = await service.CleanAsync(await ScanAndTickAsync(service));

        Assert.Equal(0, result.BytesFreed);
        Assert.Null(result.RecycleBinEmptied);
        Assert.Equal([$"{BucketName}: the Recycle Bin could not be emptied."], result.Errors);
    }

    [Fact]
    public async Task Clean_UsesTheScansFigure_WhenTheShellCouldNotSayWhatTheBinHeld()
    {
        using var roots = new TempCleanupRoots();
        var shell = Bin(new RecycleBinContents(3000, 3), new RecycleBinEmptyResult(Emptied: true, Before: null));
        var service = new DeepCleanupService(roots, recycleBin: shell);

        var result = await service.CleanAsync(await ScanAndTickAsync(service));

        Assert.Equal(3000, result.BytesFreed);
        Assert.Equal(new RecycleBinContents(3000, 3), result.RecycleBinEmptied);
    }

    [Fact]
    public async Task Clean_WithoutABin_EmptiesNothing_AndSaysSo()
    {
        // The negative half of the seam: a service built without a bin cannot reach the real one.
        var category = new CleanupCategory
        {
            Name = BucketName,
            Description = "",
            Paths = [],
            TotalSizeBytes = 3000,
            FileCount = 3,
            IsRecycleBin = true,
            IsSelected = true,
        };
        using var roots = new TempCleanupRoots();

        var result = await new DeepCleanupService(roots).CleanAsync([category]);

        Assert.Equal(0, result.BytesFreed);
        Assert.Null(result.RecycleBinEmptied);
        Assert.Equal([$"{BucketName}: not emptied, because the Recycle Bin is not available here."], result.Errors);
    }
}
