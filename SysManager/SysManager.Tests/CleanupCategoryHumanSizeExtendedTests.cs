// SysManager · FormatHelper.FormatSize extended boundary tests
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

public class CleanupCategoryHumanSizeExtendedTests
{
    [Fact]
    public void HumanSize_Zero_ReturnsZeroB()
    {
        Assert.Equal("0 B", FormatHelper.FormatSize(0));
    }

    [Fact]
    public void HumanSize_Negative_ReturnsZeroB()
    {
        Assert.Equal("0 B", FormatHelper.FormatSize(-100));
    }

    [Fact]
    public void HumanSize_OneByte_Returns1B()
    {
        Assert.Equal("1 B", FormatHelper.FormatSize(1));
    }

    [Fact]
    public void HumanSize_1023Bytes_Returns1023B()
    {
        Assert.Equal("1023 B", FormatHelper.FormatSize(1023));
    }

    [Fact]
    public void HumanSize_1024Bytes_Returns1KB()
    {
        Assert.Equal("1.0 KB", FormatHelper.FormatSize(1024));
    }

    [Fact]
    public void HumanSize_1MB_Returns1MB()
    {
        Assert.Equal("1.0 MB", FormatHelper.FormatSize(1024 * 1024));
    }

    [Fact]
    public void HumanSize_1GB_Returns1GB()
    {
        Assert.Equal("1.0 GB", FormatHelper.FormatSize(1024L * 1024 * 1024));
    }

    [Fact]
    public void HumanSize_1TB_Returns1TB()
    {
        Assert.Equal("1.0 TB", FormatHelper.FormatSize(1024L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void HumanSize_1point5GB_FormatsCorrectly()
    {
        long bytes = (long)(1.5 * 1024 * 1024 * 1024);
        Assert.Equal("1.5 GB", FormatHelper.FormatSize(bytes));
    }

    [Fact]
    public void HumanSize_LargeValue_StaysInTB()
    {
        long bytes = 10L * 1024 * 1024 * 1024 * 1024;
        Assert.Equal("10.0 TB", FormatHelper.FormatSize(bytes));
    }

    [Fact]
    public void HumanSize_MaxLong_DoesNotThrow()
    {
        var result = FormatHelper.FormatSize(long.MaxValue);
        Assert.Contains("TB", result);
    }

    [Fact]
    public void CleanupResult_Summary_NoErrors_FormatsCorrectly()
    {
        var result = new CleanupResult
        {
            BytesFreed = 1024 * 1024 * 50,
            FilesDeleted = 100
        };
        Assert.Contains("50 MB", result.Summary);
        Assert.Contains("100", result.Summary);
        Assert.DoesNotContain("skipped", result.Summary);
    }

    [Fact]
    public void CleanupResult_Summary_WithErrors_ShowsSkipped()
    {
        var result = new CleanupResult
        {
            BytesFreed = 1024 * 1024,
            FilesDeleted = 10,
            Errors = new[] { "err1", "err2" }
        };
        Assert.Contains("2 skipped", result.Summary);
    }

    [Fact]
    public void LargeFileEntry_SizeDisplay_UsesHumanSize()
    {
        var entry = new LargeFileEntry
        {
            Path = @"C:\test\bigfile.iso",
            Name = "bigfile.iso",
            SizeBytes = 4_700_000_000,
            LastModified = new DateTime(2024, 6, 15)
        };
        Assert.Contains("GB", entry.SizeDisplay);
    }

    [Fact]
    public void LargeFileEntry_LastModifiedDisplay_FormatsCorrectly()
    {
        var entry = new LargeFileEntry
        {
            Path = @"C:\test\file.zip",
            Name = "file.zip",
            SizeBytes = 1000,
            LastModified = new DateTime(2024, 3, 1)
        };
        Assert.Contains("2024", entry.LastModifiedDisplay);
        // Use the expected month name from the current culture to avoid locale failures
        var expectedMonth = new DateTime(2024, 3, 1).ToString("MMM", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Contains(expectedMonth, entry.LastModifiedDisplay);
    }

    [Fact]
    public void CleanupCategory_SizeDisplay_ShowsHumanSize()
    {
        var cat = new CleanupCategory
        {
            Name = "Test",
            Description = "Test category",
            Paths = new[] { @"C:\temp" },
            TotalSizeBytes = 2048,
            FileCount = 5
        };
        Assert.Equal("2.0 KB", cat.SizeDisplay);
    }

    [Fact]
    public void CleanupCategory_CountDisplay_FormatsWithCommas()
    {
        var cat = new CleanupCategory
        {
            Name = "Test",
            Description = "Test category",
            Paths = new[] { @"C:\temp" },
            TotalSizeBytes = 0,
            FileCount = 1500
        };
        Assert.Contains("1,500", cat.CountDisplay);
    }
}
