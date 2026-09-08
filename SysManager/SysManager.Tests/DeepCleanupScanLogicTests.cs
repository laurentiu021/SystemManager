// SysManager · DeepCleanupScanLogicTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// What Deep Cleanup's scan DECIDES, asserted against a tree the test builds — exact file counts, exact
/// byte totals, the age cutoff, which categories arrive pre-selected.
/// </summary>
/// <remarks>
/// None of this was assertable before <see cref="ICleanupRoots"/> (#2176). The scan read
/// <c>Environment.GetFolderPath</c> and <c>DriveInfo.GetDrives()</c> directly, so a test could only check
/// shapes that hold on any machine — the name is non-empty, the size is non-negative — which pass for
/// reasons nobody chose, and pass over almost-empty caches on a fresh CI runner while a real workstation
/// has unpredictable content in them.
/// <para>Everything below a root is still the real filesystem. The seam redirects WHERE the scan looks,
/// not HOW it reads, so size aggregation, the age cutoff and reparse-point skipping are exercised against
/// real directories rather than a fake — which is the whole reason the seam is the roots and not an
/// <c>IFileSystem</c>.</para>
/// <para>Category names are quoted from the definitions on purpose. If one is renamed, these fail loudly
/// with "no category named X" rather than silently asserting over an empty list, which is the failure mode
/// the shared-scan tests have by construction.</para>
/// </remarks>
public sealed class DeepCleanupScanLogicTests
{
    /// <summary>Fresh roots per test: an exact byte total cannot survive a tree another test planted in.</summary>
    private static TempCleanupRoots Roots() => new();

    private static void WriteFile(string path, int bytes) => TempCleanupRoots.WriteFile(path, bytes);

    private static CleanupCategory Named(IReadOnlyList<CleanupCategory> categories, string name)
    {
        var found = categories.FirstOrDefault(c => c.Name == name);
        Assert.NotNull(found);   // renamed or removed — fix this test rather than letting it assert nothing
        return found!;
    }

    // ── Sizes and counts ─────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_ReportsTheExactFileCountAndByteTotal()
    {
        using var roots = Roots();
        var amd = Path.Combine(roots.SystemDrive, "AMD");
        WriteFile(Path.Combine(amd, "a.bin"), 10);
        WriteFile(Path.Combine(amd, "b.bin"), 10);
        WriteFile(Path.Combine(amd, "nested", "c.bin"), 10);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "AMD installer leftovers");
        Assert.Equal(3, category.FileCount);
        Assert.Equal(30, category.TotalSizeBytes);
        Assert.Equal(0, category.SkippedCount);
    }

    [Fact]
    public async Task Scan_WithContent_PreSelectsTheCategory()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.SystemDrive, "AMD", "a.bin"), 1);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        Assert.True(Named(categories, "AMD installer leftovers").IsSelected);
    }

    /// <summary>
    /// An empty folder is still reported, and must NOT arrive ticked — a "clean 0 bytes" run tells the
    /// user nothing happened for a reason they cannot see.
    /// </summary>
    [Fact]
    public async Task Scan_WithAnEmptyFolder_ReportsZeroAndDoesNotSelectIt()
    {
        using var roots = Roots();
        Directory.CreateDirectory(Path.Combine(roots.SystemDrive, "Intel"));

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Intel driver extracts");
        Assert.Equal(0, category.FileCount);
        Assert.Equal(0, category.TotalSizeBytes);
        Assert.False(category.IsSelected);
    }

    [Fact]
    public async Task Scan_WithNoFolderAtAll_ReportsNoPaths()
    {
        using var roots = Roots();
        var categories = await new DeepCleanupService(roots).ScanAsync();

        Assert.Empty(Named(categories, "Intel driver extracts").Paths);
    }

    // ── The age cutoff ───────────────────────────────────────────────────────

    /// <summary>
    /// The servicing-logs category counts only files older than thirty days, and the boundary is what a
    /// test has to pin: an off-by-one in the comparison would either keep everything or delete a log
    /// Windows is still rolling.
    /// </summary>
    /// <remarks>
    /// <b>The two files are deliberately different sizes.</b> With both at 100 bytes this test passed
    /// against an INVERTED comparison — one file either way, 100 bytes either way — so it asserted that
    /// exactly one file was counted without asserting which. Distinct sizes make the byte total name the
    /// file, and the mutation that swapped the comparison then fails. Found by the proof, not by review.
    /// </remarks>
    [Fact]
    public async Task Scan_WithAnAgeCutoff_CountsOnlyWhatIsOlderThanIt()
    {
        using var roots = Roots();
        var cbs = Path.Combine(roots.WindowsDirectory, "Logs", "CBS");
        var old = Path.Combine(cbs, "old.log");
        var young = Path.Combine(cbs, "young.log");
        WriteFile(old, 100);
        WriteFile(young, 7);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(31));
        File.SetLastWriteTimeUtc(young, DateTime.UtcNow - TimeSpan.FromDays(29));

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Old Windows servicing logs (> 30 days)");
        Assert.Equal(1, category.FileCount);
        Assert.Equal(100, category.TotalSizeBytes);   // 7 would mean it kept the young one instead
        Assert.Equal(TimeSpan.FromDays(30), category.OlderThan);
    }

    // ── Windows.old ──────────────────────────────────────────────────────────

    /// <summary>
    /// Windows.old appears only when it exists, and never arrives ticked however much it holds — it is
    /// the user's way back to their previous Windows, so pre-selecting it would let one click destroy
    /// the rollback.
    /// </summary>
    [Fact]
    public async Task Scan_WithWindowsOld_OffersItButNeverPreSelectsIt()
    {
        using var roots = Roots();
        WriteFile(Path.Combine(roots.SystemDrive, "Windows.old", "big.bin"), 5000);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        var category = Named(categories, "Windows.old (previous Windows installation)");
        Assert.Equal(5000, category.TotalSizeBytes);
        Assert.True(category.IsDestructiveHint);
        Assert.False(category.IsSelected, "pre-ticking this would let one click destroy the rollback");
    }

    [Fact]
    public async Task Scan_WithoutWindowsOld_DoesNotOfferIt()
    {
        using var roots = Roots();
        var categories = await new DeepCleanupService(roots).ScanAsync();

        Assert.DoesNotContain(categories, c => c.Name.Contains("Windows.old", StringComparison.Ordinal));
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_ReportsProgressForEveryCategoryAndFinishesOnDone()
    {
        using var roots = Roots();
        var progress = new SyncProgress<DeepCleanupService.ScanProgress>();

        var categories = await new DeepCleanupService(roots).ScanAsync(progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal("Done", progress.Reports[^1].CategoryName);
        Assert.Equal(progress.Reports[^1].Total, progress.Reports[^1].Current);

        // One report per category plus the final "Done": a bar that stops short of its own total reads
        // as a stalled scan.
        Assert.Equal(categories.Count + 1, progress.Reports.Count);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    /// <summary>
    /// A cancelled scan throws rather than returning what it had. Returning partial results would let the
    /// caller show a success toast for a scan the user stopped.
    /// </summary>
    [Fact]
    public async Task Scan_WhenCancelledBeforeItStarts_Throws()
    {
        using var roots = Roots();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DeepCleanupService(roots).ScanAsync(ct: cts.Token));
    }

    // ── Guarding the seam itself ─────────────────────────────────────────────

    [Fact]
    public void Constructor_WithoutRoots_Throws()
        => Assert.Throws<ArgumentNullException>(() => new DeepCleanupService(null!));

    /// <summary>
    /// The default roots are the values the scan read inline before the seam existed, so production
    /// behaviour is unchanged by construction rather than by inspection.
    /// </summary>
    /// <remarks>
    /// <c>FixedDriveRoots</c> is asserted to be read FRESH rather than cached, because
    /// <c>DeepCleanupService</c> is a DI singleton built once at startup: caching would freeze the drive
    /// list for the whole session, so a drive plugged in afterwards would silently stop being scanned.
    /// That is the one member where the obvious implementation is the wrong one.
    /// </remarks>
    [Fact]
    public void SystemCleanupRoots_MatchesWhatTheScanUsedToReadInline()
    {
        var roots = new SystemCleanupRoots();

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), roots.LocalAppData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), roots.ProgramData);
        Assert.Equal(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", roots.SystemDrive);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), roots.WindowsDirectory);
        Assert.Equal(Path.GetTempPath(), roots.UserTemp);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), roots.ProgramFilesX86);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), roots.ProgramFiles);

        var expected = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);
        Assert.Equal(expected, roots.FixedDriveRoots);
        Assert.Equal(RecycleBinHelper.CurrentUserBinPaths(), roots.RecycleBinPaths);

        Assert.NotSame(roots.FixedDriveRoots, roots.FixedDriveRoots);
        Assert.NotSame(roots.RecycleBinPaths, roots.RecycleBinPaths);
    }
}
