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

    // ── NormalizePath: rundll32 / msiexec ──

    [Fact]
    public void NormalizePath_Rundll32_ExtractsDll()
    {
        var result = IconExtractorService.NormalizePath(
            @"C:\Windows\System32\rundll32.exe shell32.dll,Control_RunDLL");
        Assert.Contains("shell32.dll", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePath_Msiexec_ReturnsMsiexec()
    {
        var result = IconExtractorService.NormalizePath(@"MsiExec.exe /X{12345-ABCDE}");
        Assert.Contains("msiexec.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── System process detection ──

    [Fact]
    public void IsWindowsSystemProcess_Csrss_True()
        => Assert.True(IconExtractorService.IsWindowsSystemProcess("csrss"));

    [Fact]
    public void IsWindowsSystemProcess_Explorer_True()
        => Assert.True(IconExtractorService.IsWindowsSystemProcess("explorer"));

    [Fact]
    public void IsWindowsSystemProcess_Discord_False()
        => Assert.False(IconExtractorService.IsWindowsSystemProcess("Discord"));

    [Fact]
    public void IsServiceProcess_Svchost_True()
        => Assert.True(IconExtractorService.IsServiceProcess("svchost"));

    [Fact]
    public void IsServiceProcess_Chrome_False()
        => Assert.False(IconExtractorService.IsServiceProcess("chrome"));

    // ── GetProcessIcon ──

    [Fact]
    public void GetProcessIcon_NullBoth_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetProcessIcon(null, null));
        Assert.Null(ex);
    }

    [Fact]
    public void GetProcessIcon_EmptyPathSystemName_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetProcessIcon("", "svchost"));
        Assert.Null(ex);
    }

    // ── GetInstalledAppIcon ──

    [Fact]
    public void GetInstalledAppIcon_AllNull_DoesNotThrow()
    {
        var ex = Record.Exception(() => IconExtractorService.GetInstalledAppIcon(null, null, null));
        Assert.Null(ex);
    }

    [Fact]
    public void GetInstalledAppIcon_BogusPath_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            IconExtractorService.GetInstalledAppIcon(@"C:\Fake\app.exe", @"C:\Fake", "FakeApp"));
        Assert.Null(ex);
    }

    // ── Contextual fallback icons ──

    [Fact]
    public void WindowsIcon_DoesNotThrow()
    {
        var ex = Record.Exception(() => { _ = IconExtractorService.WindowsIcon; });
        Assert.Null(ex);
    }

    [Fact]
    public void GearIcon_DoesNotThrow()
    {
        var ex = Record.Exception(() => { _ = IconExtractorService.GearIcon; });
        Assert.Null(ex);
    }
}
