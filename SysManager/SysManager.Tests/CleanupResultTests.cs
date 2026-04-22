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
}
