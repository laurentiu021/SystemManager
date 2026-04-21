using SysManager.Models;

namespace SysManager.IntegrationTests;

public class CleanupCategoryTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1 TB")]
    public void HumanSize_FormatsCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, CleanupCategory.HumanSize(bytes));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-1024L)]
    [InlineData(long.MinValue)]
    public void HumanSize_NegativeIsZero(long bytes)
    {
        Assert.Equal("0 B", CleanupCategory.HumanSize(bytes));
    }

    [Fact]
    public void HumanSize_VeryLarge_DoesNotCrash()
    {
        var s = CleanupCategory.HumanSize(long.MaxValue);
        Assert.NotNull(s);
        Assert.Contains("TB", s);
    }

    [Fact]
    public void Category_DefaultSelectable_IsFalse()
    {
        var c = new CleanupCategory
        {
            Name = "X",
            Description = "Y",
            Paths = Array.Empty<string>()
        };
        Assert.False(c.IsSelected);
        Assert.False(c.IsDestructiveHint);
        Assert.Null(c.OlderThan);
    }

    [Fact]
    public void Category_CountDisplay_FormatsThousands()
    {
        var c = new CleanupCategory
        {
            Name = "X", Description = "Y", Paths = Array.Empty<string>(),
            FileCount = 12345
        };
        Assert.Equal("12,345 files", c.CountDisplay);
    }

    [Fact]
    public void Category_SizeDisplay_UsesHumanSize()
    {
        var c = new CleanupCategory
        {
            Name = "X", Description = "Y", Paths = Array.Empty<string>(),
            TotalSizeBytes = 2048
        };
        Assert.Equal("2 KB", c.SizeDisplay);
    }

    [Fact]
    public void Category_IsSelected_Mutable()
    {
        var c = new CleanupCategory
        {
            Name = "X", Description = "Y", Paths = Array.Empty<string>()
        };
        c.IsSelected = true;
        Assert.True(c.IsSelected);
    }

    [Fact]
    public void Category_IsSelected_Mutation_RaisesChange()
    {
        var c = new CleanupCategory { Name = "X", Description = "Y", Paths = Array.Empty<string>() };
        var fired = false;
        c.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupCategory.IsSelected)) fired = true;
        };
        c.IsSelected = true;
        Assert.True(fired);
    }

    [Fact]
    public void Category_OlderThan_Settable()
    {
        var c = new CleanupCategory
        {
            Name = "X", Description = "Y", Paths = Array.Empty<string>(),
            OlderThan = TimeSpan.FromDays(30)
        };
        Assert.Equal(TimeSpan.FromDays(30), c.OlderThan);
    }

    [Fact]
    public void CleanupResult_DefaultsZero()
    {
        var r = new CleanupResult();
        Assert.Equal(0, r.BytesFreed);
        Assert.Equal(0, r.FilesDeleted);
        Assert.Empty(r.Errors);
        Assert.Contains("Freed", r.Summary);
    }

    [Fact]
    public void CleanupResult_Summary_IncludesSkipped()
    {
        var r = new CleanupResult
        {
            BytesFreed = 1024,
            FilesDeleted = 2,
            Errors = new[] { "err1" }
        };
        Assert.Contains("skipped", r.Summary);
    }

    [Fact]
    public void CleanupResult_Summary_NoSkippedWhenNoErrors()
    {
        var r = new CleanupResult { BytesFreed = 1, FilesDeleted = 1 };
        Assert.DoesNotContain("skipped", r.Summary);
    }

    [Fact]
    public void LargeFileEntry_SizeDisplay_UsesHumanSize()
    {
        var e = new LargeFileEntry
        {
            Path = @"C:\x",
            Name = "x",
            SizeBytes = 2048,
            LastModified = DateTime.Now
        };
        Assert.Equal("2 KB", e.SizeDisplay);
    }

    [Fact]
    public void LargeFileEntry_LastModifiedDisplay_NonEmpty()
    {
        var e = new LargeFileEntry
        {
            Path = @"C:\x",
            Name = "x",
            SizeBytes = 1,
            LastModified = new DateTime(2026, 4, 20)
        };
        Assert.Contains("2026", e.LastModifiedDisplay);
    }
}
