// SysManager · IconExtractorServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="IconExtractorService"/> — path normalization,
/// caching behavior, and fallback handling.
/// </summary>
public class IconExtractorServiceTests
{
    // ── NormalizePath ──

    [Fact]
    public void NormalizePath_Null_ReturnsEmpty()
        => Assert.Equal(string.Empty, IconExtractorService.NormalizePath(null!));

    [Fact]
    public void NormalizePath_Empty_ReturnsEmpty()
        => Assert.Equal(string.Empty, IconExtractorService.NormalizePath(""));

    [Fact]
    public void NormalizePath_Whitespace_ReturnsEmpty()
        => Assert.Equal(string.Empty, IconExtractorService.NormalizePath("   "));

    [Fact]
    public void NormalizePath_PlainExe_ExpandsEnvironmentVars()
    {
        var result = IconExtractorService.NormalizePath(@"%SystemRoot%\explorer.exe");
        Assert.Contains("explorer.exe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%SystemRoot%", result);
    }

    [Fact]
    public void NormalizePath_QuotedPath_StripsQuotes()
    {
        var result = IconExtractorService.NormalizePath("\"C:\\Windows\\explorer.exe\"");
        Assert.DoesNotContain("\"", result);
    }

    [Fact]
    public void NormalizePath_PathWithArgs_ExtractsExe()
    {
        var result = IconExtractorService.NormalizePath(@"C:\Windows\explorer.exe /n,/e");
        Assert.EndsWith("explorer.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePath_QuotedPathWithArgs_ExtractsExe()
    {
        var result = IconExtractorService.NormalizePath("\"C:\\Windows\\explorer.exe\" /n,/e");
        Assert.EndsWith("explorer.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePath_NonExistentPath_ReturnsAsIs()
    {
        var bogus = @"C:\NonExistent\FakeApp12345.exe";
        var result = IconExtractorService.NormalizePath(bogus);
        Assert.Contains("FakeApp12345.exe", result);
    }

    [Fact]
    public void NormalizePath_PathWithDashArgs_ExtractsExe()
    {
        var result = IconExtractorService.NormalizePath(@"C:\Windows\explorer.exe --some-flag");
        Assert.EndsWith("explorer.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetIcon edge cases ──

    [Fact]
    public void GetIcon_Null_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetIcon(null));
        Assert.Null(ex);
    }

    [Fact]
    public void GetIcon_Empty_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetIcon(""));
        Assert.Null(ex);
    }

    [Fact]
    public void GetIcon_Whitespace_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetIcon("   "));
        Assert.Null(ex);
    }

    [Fact]
    public void GetIcon_NonExistentPath_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetIcon(@"C:\NonExistent\FakeApp12345.exe"));
        Assert.Null(ex);
    }

    // ── Cache ──

    [Fact]
    public void ClearCache_ResetsCount()
    {
        IconExtractorService.ClearCache();
        Assert.Equal(0, IconExtractorService.CacheCount);
    }

    [Fact]
    public void GetIcon_SamePath_UsesCachedResult()
    {
        IconExtractorService.ClearCache();
        var bogus = @"C:\NonExistent\CacheTest12345.exe";
        _ = IconExtractorService.GetIcon(bogus);
        var countAfterFirst = IconExtractorService.CacheCount;
        _ = IconExtractorService.GetIcon(bogus);
        Assert.Equal(countAfterFirst, IconExtractorService.CacheCount);
    }

    // ── Model Icon property defaults ──

    [Fact]
    public void ProcessEntry_Icon_DefaultNull()
    {
        var entry = new Models.ProcessEntry();
        Assert.Null(entry.Icon);
    }

    [Fact]
    public void StartupEntry_Icon_DefaultNull()
    {
        var entry = new Models.StartupEntry();
        Assert.Null(entry.Icon);
    }

    [Fact]
    public void InstalledApp_Icon_DefaultNull()
    {
        var app = new Models.InstalledApp();
        Assert.Null(app.Icon);
    }
}
