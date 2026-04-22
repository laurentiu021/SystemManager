// SysManager · DeepCleanupServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class DeepCleanupServiceTests
{
    [Fact]
    public void Constructs()
    {
        var s = new DeepCleanupService();
        Assert.NotNull(s);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNonNull()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.NotNull(r);
    }

    [Fact]
    public async Task ScanAsync_ReturnsSeveralCategories()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        // System + gaming categories should always be scanned even if empty.
        Assert.True(r.Count >= 10, $"Expected >=10 categories, got {r.Count}");
    }

    [Fact]
    public async Task ScanAsync_AllCategoriesHaveName()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.All(r, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public async Task ScanAsync_AllCategoriesHaveDescription()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.All(r, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public async Task ScanAsync_AllSizesNonNegative()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.All(r, c => Assert.True(c.TotalSizeBytes >= 0));
    }

    [Fact]
    public async Task ScanAsync_AllCountsNonNegative()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.All(r, c => Assert.True(c.FileCount >= 0));
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
    public async Task ScanAsync_IncludesNvidiaCategory()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesAmdCategory()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesIntelCategory()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesWindowsUpdateCache()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Windows Update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesTempFiles()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Temporary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesPrefetch()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Prefetch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesRecycleBin()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Recycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesSteam()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesEpic()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Epic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesBattleNet()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Battle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesRiot()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Riot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesGog()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("GOG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesEaApp()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("EA ", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesDirectXShaderCache()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("DirectX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesCrashDumps()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Crash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesPatchCache()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Installer patch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_IncludesDeliveryOptimization()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.Contains(r, c => c.Name.Contains("Delivery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_CategoriesHaveUniqueNames()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        var names = r.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task ScanAsync_WindowsOldNeverSelectedByDefault()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        var wo = r.FirstOrDefault(c => c.Name.Contains("Windows.old", StringComparison.OrdinalIgnoreCase));
        if (wo != null)
        {
            Assert.False(wo.IsSelected, "Windows.old must never auto-select");
            Assert.True(wo.IsDestructiveHint);
        }
    }

    [Fact]
    public async Task ScanAsync_EmptyCategoriesAreNotSelected()
    {
        var s = new DeepCleanupService();
        var r = await s.ScanAsync();
        Assert.All(r, c =>
        {
            if (c.TotalSizeBytes == 0) Assert.False(c.IsSelected);
        });
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
        var s = new DeepCleanupService();
        var cats = await s.ScanAsync();
        foreach (var c in cats) c.IsSelected = false;
        var r = await s.CleanAsync(cats);
        Assert.Equal(0, r.BytesFreed);
        Assert.Equal(0, r.FilesDeleted);
    }

    [Fact]
    public async Task CleanAsync_CancelledToken_ReturnsImmediately()
    {
        var s = new DeepCleanupService();
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
}
