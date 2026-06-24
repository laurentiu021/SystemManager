// SysManager · BiosServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BiosService"/>'s pure logic — the manufacturer support-URL
/// resolver and the WMI date formatter. The live <see cref="BiosService.Read"/> hits
/// WMI/registry and is not unit-tested.
/// </summary>
public class BiosServiceTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "https://www.asus.com/support/")]
    [InlineData("Micro-Star International Co., Ltd.", "https://www.msi.com/support")]
    [InlineData("MSI", "https://www.msi.com/support")]
    [InlineData("Gigabyte Technology Co., Ltd.", "https://www.gigabyte.com/Support")]
    [InlineData("ASRock", "https://www.asrock.com/support/")]
    [InlineData("Dell Inc.", "https://www.dell.com/support/home")]
    [InlineData("HP", "https://support.hp.com")]
    [InlineData("Hewlett-Packard", "https://support.hp.com")]
    [InlineData("LENOVO", "https://pcsupport.lenovo.com")]
    public void SupportUrl_ResolvesKnownManufacturers(string manufacturer, string expected)
        => Assert.Equal(expected, BiosService.SupportUrl(manufacturer, "X570"));

    [Fact]
    public void SupportUrl_UnknownManufacturer_FallsBackToWebSearch()
    {
        var url = BiosService.SupportUrl("Acme Boards", "SuperBoard 9000");
        Assert.StartsWith("https://www.google.com/search?q=", url);
        Assert.Contains("BIOS", url);
        Assert.Contains("Acme", url);
    }

    [Fact]
    public void SupportUrl_NullManufacturer_DoesNotThrow()
    {
        var url = BiosService.SupportUrl(null!, "Board");
        Assert.StartsWith("https://www.google.com/search?q=", url);
    }

    [Theory]
    [InlineData("20240115000000.000000+000", "2024-01-15")]
    [InlineData("20231231", "2023-12-31")]
    public void FormatWmiDate_ParsesCimDateTime(string cim, string expected)
        => Assert.Equal(expected, BiosService.FormatWmiDate(cim));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("notadateXX")]
    public void FormatWmiDate_InvalidInput_ReturnsEmpty(string? cim)
        => Assert.Equal("", BiosService.FormatWmiDate(cim));

    [Fact]
    public void BiosInfo_BoardDisplay_CombinesManufacturerAndProduct()
    {
        var b = new BiosInfo("1.2.0", "2024-01-15", "AMI", "UEFI", "On", "ASUSTeK", "ROG STRIX X570-E");
        Assert.Equal("ASUSTeK ROG STRIX X570-E", b.BoardDisplay);
        Assert.False(b.IsEmpty);
    }

    [Fact]
    public void BiosInfo_IsEmpty_WhenNoData()
    {
        var b = new BiosInfo("", "", "", "Unknown", "Unknown", "", "");
        Assert.True(b.IsEmpty);
    }
}
