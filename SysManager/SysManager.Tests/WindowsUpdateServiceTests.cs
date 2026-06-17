// SysManager · WindowsUpdateServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsUpdateService"/>'s pure classification/formatting
/// helpers. The COM-lifetime fixes (releasing every IUpdate child RCW) can't be unit
/// tested without a live WUA feed, but these tests pin that the title-only
/// classification path — restructured when the Categories collection release was
/// wrapped in try/finally — still behaves identically.
/// </summary>
public class WindowsUpdateServiceTests
{
    [Theory]
    [InlineData("2024-06 Cumulative Update for Windows 11", "Cumulative")]
    [InlineData("Security Update for Microsoft Windows", "Security")]
    [InlineData("Windows Malicious Software Removal Tool - Defender", "Defender")]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus", "Defender")]
    [InlineData("NVIDIA driver update", "Driver")]
    [InlineData("Intel - Firmware", "Driver")]
    [InlineData("Servicing Stack Update for Windows", "Servicing")]
    [InlineData("Feature update to Windows 11", "Update")]
    [InlineData("", "Update")]
    public void ClassifyCategory_TitleOnly_ClassifiesByKeyword(string title, string expected)
    {
        // u = null exercises the title-only path (no COM Categories collection).
        Assert.Equal(expected, WindowsUpdateService.ClassifyCategory(title, null));
    }

    [Fact]
    public void ClassifyCategory_DotNetSecurity_PrefersSecurity()
    {
        // "Security Update" is checked before ".NET", so a .NET security update is Security.
        Assert.Equal("Security", WindowsUpdateService.ClassifyCategory(".NET 8 Security Update", null));
    }

    [Fact]
    public void ClassifyCategory_DotNetNonSecurity_IsDotNet()
    {
        Assert.Equal(".NET", WindowsUpdateService.ClassifyCategory("Update for .NET Runtime", null));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(157286400, "150.0 MB")]
    [InlineData(2147483648, "2.0 GB")]
    public void FormatSize_FormatsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, WindowsUpdateService.FormatSize(bytes));
    }
}
