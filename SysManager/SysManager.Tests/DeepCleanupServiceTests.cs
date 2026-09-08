// SysManager · DeepCleanupServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// One scan of a temp tree, shared by every test that only READS the catalogue.
/// </summary>
/// <remarks>
/// <b>This used to scan the real machine, and it no longer does.</b> #2167 cut 27 serial disk walks to one
/// — on a machine with real caches, 16 of these tests were measured at 31 to 52 seconds EACH, and the class
/// alone did not finish inside a 20-minute budget while every other class had completed. That fixed the
/// cost and left the non-determinism: 27 tests depending on one scan of one machine, so the blocking
/// suite's result was still a function of what was on the disk running it.
/// <para>#2176 added <c>ICleanupRoots</c>, and pointing the fixture at a temp tree turns out to cost these
/// tests nothing, because almost all of them are CATALOGUE assertions — <c>ScanAsync_IncludesSteam</c>,
/// unique names, the <c>&gt;= 10</c> floor — and a category is in the definitions whether or not its folder
/// exists on this machine. The scan drops from about 180 seconds here to milliseconds and stops depending
/// on the host at all.</para>
/// <para>What that made redundant went with it. <c>ScanAsync_ReturnsNonNull</c> asserted a method returns
/// something; <c>ScanAsync_WindowsOldNeverSelectedByDefault</c> opened with <c>if (wo != null)</c>, so on
/// any machine without a previous Windows install — most of them, including CI — it asserted nothing at
/// all; <c>ScanAsync_EmptyCategoriesAreNotSelected</c> could only check whatever happened to be empty.
/// <c>DeepCleanupScanLogicTests</c> now asserts all three properly, over folders it creates.</para>
/// <para>Three tests still scan for themselves, and none of them is a machine walk any more.
/// The two cancellation tests must drive <c>ScanAsync</c> directly, because cancellation is the thing under
/// test. <c>CleanAsync_NoneSelected_DoesNothing</c> MUTATES <c>IsSelected</c> on what it is given and
/// <c>CleanAsync_CancelledToken_ReturnsImmediately</c> hands its list to <c>CleanAsync</c>, so neither may
/// be given the shared instance.</para>
/// <para>The tree is planted with a <c>Windows.old</c> folder, because that category is the one definition
/// added conditionally — without it the catalogue this fixture describes would be one category short of
/// what the app can show.</para>
/// </remarks>
public sealed class DeepCleanupScanFixture : IAsyncLifetime
{
    private readonly TempCleanupRoots _roots = new();

    /// <summary>The one scan's categories. Treat as read-only — every consumer shares this instance.</summary>
    public IReadOnlyList<CleanupCategory> Categories { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        // Windows.old is the one conditional definition, so the catalogue is short one category without it.
        TempCleanupRoots.WriteFile(Path.Combine(_roots.SystemDrive, "Windows.old", "leftover.bin"), 16);
        Categories = await new DeepCleanupService(_roots).ScanAsync();
    }

    public ValueTask DisposeAsync()
    {
        _roots.Dispose();
        return ValueTask.CompletedTask;
    }
}

public class DeepCleanupServiceTests(DeepCleanupScanFixture scan) : IClassFixture<DeepCleanupScanFixture>
{
    // The shared scan. Read only: a test that needs to change a category scans for itself.
    private readonly IReadOnlyList<CleanupCategory> _scanned = scan.Categories;

    [Fact]
    public void Constructs()
    {
        var s = new DeepCleanupService();
        Assert.NotNull(s);
    }

    [Fact]
    public void ScanAsync_ReturnsSeveralCategories()
    {
        // System + gaming categories should always be scanned even if empty. Also the floor that keeps
        // every other test in this class from passing over an empty shared scan.
        Assert.True(_scanned.Count >= 10, $"Expected >=10 categories, got {_scanned.Count}");
    }

    [Fact]
    public void ScanAsync_AllCategoriesHaveName()
    {
        Assert.All(_scanned, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public void ScanAsync_AllCategoriesHaveDescription()
    {
        Assert.All(_scanned, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void ScanAsync_AllSizesNonNegative()
    {
        Assert.All(_scanned, c => Assert.True(c.TotalSizeBytes >= 0));
    }

    [Fact]
    public void ScanAsync_AllCountsNonNegative()
    {
        Assert.All(_scanned, c => Assert.True(c.FileCount >= 0));
    }

    [Fact]
    public async Task ScanAsync_RespectsCancellation()
    {
        var s = new DeepCleanupService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Task.Run(..., cancelledToken) throws TaskCanceledException — that's
        // the "no work happens" contract we want.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => s.ScanAsync(ct: cts.Token));
    }

    [Fact]
    public async Task ScanAsync_CancelledMidScan_Throws_DoesNotReturnPartial()
    {
        // Regression (F12): a scan cancelled AFTER it starts used to `break` out of its
        // loops and fall through to report "Done" + return the partial category list, so
        // the ViewModel showed a success toast for a cancelled scan. It must now throw
        // OperationCanceledException instead. Deterministic (no wall-clock): the progress
        // callback fires synchronously inside the scan loop, so cancelling on the first
        // report cancels mid-scan reliably.
        using var roots = new TempCleanupRoots();
        var s = new DeepCleanupService(roots);
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cts);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => s.ScanAsync(progress, cts.Token));
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<DeepCleanupService.ScanProgress>
    {
        public void Report(DeepCleanupService.ScanProgress value) => cts.Cancel();
    }

    [Fact]
    public void ScanAsync_IncludesNvidiaCategory()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesAmdCategory()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesIntelCategory()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesWindowsUpdateCache()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Windows Update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesTempFiles()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Temporary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesPrefetch()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Prefetch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesRecycleBin()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Recycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_RecycleBinCategory_IsFlaggedForShellApi()
    {
        // The Recycle Bin must be emptied through the shell API (SHEmptyRecycleBin),
        // not the generic file-delete path which corrupts the per-SID bin metadata.
        // CleanAsync routes on IsRecycleBin, so a regression that drops the flag would
        // silently send the bin back to raw delete. Pin the flag here.
        var bin = _scanned.FirstOrDefault(c => c.Name.Contains("Recycle", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bin);
        Assert.True(bin!.IsRecycleBin, "Recycle Bin category must be flagged IsRecycleBin so cleanup uses the shell API");
        // And no other category should carry the flag.
        Assert.All(_scanned.Where(c => !c.Name.Contains("Recycle", StringComparison.OrdinalIgnoreCase)),
            c => Assert.False(c.IsRecycleBin));
    }

    [Fact]
    public void ScanAsync_IncludesSteam()
    {
        Assert.Contains(_scanned, c => c.Name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesEpic()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Epic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesBattleNet()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Battle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesRiot()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Riot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesGog()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("GOG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesEaApp()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("EA ", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesDirectXShaderCache()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("DirectX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesCrashDumps()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Crash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesPatchCache()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Installer patch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_IncludesDeliveryOptimization()
    {
        Assert.Contains(_scanned, c => c.Name.Contains("Delivery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanAsync_CategoriesHaveUniqueNames()
    {
        var names = _scanned.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task CleanAsync_EmptyList_ReturnsZero()
    {
        var s = new DeepCleanupService();
        var r = await s.CleanAsync(new List<CleanupCategory>());
        Assert.Equal(0, r.BytesFreed);
        Assert.Equal(0, r.FilesDeleted);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task CleanAsync_NoneSelected_DoesNothing()
    {
        // Its own scan, not the class fixture's: this test clears IsSelected on every category it is
        // given, and the fixture's list is shared with every read-only test in the same collection.
        // Its own ROOTS too — this was one of the two remaining real machine walks in the class, and on a
        // workstation with real caches it alone accounted for most of the runtime.
        using var roots = new TempCleanupRoots();
        var s = new DeepCleanupService(roots);
        var cats = await s.ScanAsync();
        foreach (var c in cats) c.IsSelected = false;
        var r = await s.CleanAsync(cats);
        Assert.Equal(0, r.BytesFreed);
        Assert.Equal(0, r.FilesDeleted);
    }

    [Fact]
    public async Task CleanAsync_CancelledToken_ReturnsImmediately()
    {
        // Its own scan for the same reason: the list is handed to CleanAsync, and a shared instance must
        // not be passed to something whose job is to act on it — even when a cancelled token means it
        // will not. Temp roots for the same reason as the test above.
        using var roots = new TempCleanupRoots();
        var s = new DeepCleanupService(roots);
        var cats = await s.ScanAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Same Task.Run(..., cancelledToken) contract — no work happens.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => s.CleanAsync(cats, ct: cts.Token));
    }

    [Fact]
    public async Task CleanAsync_DeletesFilesInTempDir()
    {
        // Create a throw-away test folder, register it as a fake category,
        // verify files are actually deleted.
        var root = Path.Combine(Path.GetTempPath(), "SysManagerDeepCleanTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var f1 = Path.Combine(root, "a.txt"); File.WriteAllText(f1, "aaaa");
        var f2 = Path.Combine(root, "b.txt"); File.WriteAllText(f2, "bbbbb");
        try
        {
            var cat = new CleanupCategory
            {
                Name = "Test",
                Description = "Test",
                Paths = new[] { root },
                TotalSizeBytes = 9,
                FileCount = 2,
                IsSelected = true
            };
            var s = new DeepCleanupService();
            var r = await s.CleanAsync(new[] { cat });
            Assert.True(r.FilesDeleted >= 2);
            Assert.True(r.BytesFreed >= 9);
            Assert.False(File.Exists(f1));
            Assert.False(File.Exists(f2));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CleanAsync_SkipsMissingPaths()
    {
        var cat = new CleanupCategory
        {
            Name = "Nope",
            Description = "Does not exist",
            Paths = new[] { Path.Combine(Path.GetTempPath(), "NoSuchDir_" + Guid.NewGuid().ToString("N")) },
            TotalSizeBytes = 0,
            FileCount = 0,
            IsSelected = true
        };
        var s = new DeepCleanupService();
        var r = await s.CleanAsync(new[] { cat });
        Assert.Equal(0, r.FilesDeleted);
    }

    [Fact]
    public async Task CleanAsync_OnlyRemovesSelected()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerDeepCleanTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var f1 = Path.Combine(root, "keep.txt"); File.WriteAllText(f1, "kept");
        try
        {
            var catSelected = new CleanupCategory
            {
                Name = "Keep me",
                Description = "Should stay",
                Paths = new[] { root },
                TotalSizeBytes = 4,
                FileCount = 1,
                IsSelected = false // deliberately unselected
            };
            var s = new DeepCleanupService();
            var r = await s.CleanAsync(new[] { catSelected });
            Assert.Equal(0, r.FilesDeleted);
            Assert.True(File.Exists(f1));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CleanAsync_OlderThanFilter_KeepsRecentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerAgeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fresh = Path.Combine(root, "fresh.log"); File.WriteAllText(fresh, "x");
        try
        {
            var cat = new CleanupCategory
            {
                Name = "Old logs",
                Description = "Should only delete > 30 day old",
                Paths = new[] { root },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true,
                OlderThan = TimeSpan.FromDays(30)
            };
            var s = new DeepCleanupService();
            var r = await s.CleanAsync(new[] { cat });
            Assert.Equal(0, r.FilesDeleted);
            Assert.True(File.Exists(fresh));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CleanAsync_DoesNotFollowJunctionOutsideTarget()
    {
        // Regression: cleanup traversal must NOT descend into reparse points
        // (junctions / symlinks). Following one would let CleanAsync delete files
        // that live outside the cleanup target tree — a data-loss bug.
        var baseDir = Path.Combine(Path.GetTempPath(), "smtest_junc_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(baseDir, "outside");   // simulates real user data
        var cleanRoot = Path.Combine(baseDir, "cache");   // the folder being cleaned
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(cleanRoot);

        var precious = Path.Combine(target, "precious.dat");
        File.WriteAllText(precious, "do not delete me");

        var link = Path.Combine(cleanRoot, "link");
        // mklink /J creates a directory junction; no admin rights required.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                proc.WaitForExit(10_000);
                if (proc.ExitCode != 0 || !IsReparse(link))
                {
                    // Environment can't create junctions (rare) — nothing to assert.
                    return;
                }
            }

            var cat = new CleanupCategory
            {
                Name = "Cache",
                Description = "Cleanup root containing a junction",
                Paths = new[] { cleanRoot },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true
            };
            var s = new DeepCleanupService();
            await s.CleanAsync(new[] { cat });

            // The file behind the junction must survive: traversal never entered it.
            Assert.True(File.Exists(precious), "Cleanup followed a junction and deleted data outside the target tree");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }

        static bool IsReparse(string p)
        {
            try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
            catch { return false; }
        }
    }

    [Fact]
    public async Task CleanAsync_DoesNotFollowJunctionAtCleanupRoot()
    {
        // Regression for the ROOT-as-junction gap: the reparse guard previously only
        // covered CHILD directories, so a junction planted AT a cleanup-root path
        // (writable without admin, e.g. %LOCALAPPDATA%\NVIDIA\GLCache) was enumerated
        // directly and the link TARGET's files were deleted — data loss outside the tree.
        var baseDir = Path.Combine(Path.GetTempPath(), "smtest_juncroot_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(baseDir, "outside");   // simulates real user data
        Directory.CreateDirectory(target);

        var precious = Path.Combine(target, "precious.dat");
        File.WriteAllText(precious, "do not delete me");

        // The cleanup ROOT itself is the junction (points at the target).
        var cleanRoot = Path.Combine(baseDir, "cacheLink");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{cleanRoot}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                proc.WaitForExit(10_000);
                if (proc.ExitCode != 0 || !IsReparse(cleanRoot))
                {
                    // Environment can't create junctions (rare) — nothing to assert.
                    return;
                }
            }

            var cat = new CleanupCategory
            {
                Name = "Cache",
                Description = "Cleanup root that IS a junction",
                Paths = new[] { cleanRoot },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true
            };
            var s = new DeepCleanupService();
            await s.CleanAsync(new[] { cat });

            // The file behind the junction-root must survive: the root reparse guard
            // stops the traversal before any file is enumerated for deletion.
            Assert.True(File.Exists(precious), "Cleanup followed a junction at the cleanup root and deleted data outside the target tree");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }

        static bool IsReparse(string p)
        {
            try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
            catch { return false; }
        }
    }
}
