// SysManager · FixedDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FixedDriveService"/> — pure-logic mapping methods.
/// The actual WMI enumeration is an integration test.
/// </summary>
public class FixedDriveServiceTests
{
    private static string InvokeMapMedia(uint v)
    {
        var m = typeof(FixedDriveService).GetMethod("MapMedia", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { v })!;
    }

    private static string InvokeMapBus(uint v)
    {
        var m = typeof(FixedDriveService).GetMethod("MapBus", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { v })!;
    }

    [Theory]
    [InlineData(3u, "HDD")]
    [InlineData(4u, "SSD")]
    [InlineData(5u, "SCM")]
    [InlineData(0u, "")]
    [InlineData(99u, "")]
    public void MapMedia_ReturnsExpected(uint input, string expected)
        => Assert.Equal(expected, InvokeMapMedia(input));

    [Theory]
    [InlineData(1u, "SCSI")]
    [InlineData(3u, "ATA")]
    [InlineData(7u, "USB")]
    [InlineData(10u, "SAS")]
    [InlineData(11u, "SATA")]
    [InlineData(17u, "NVMe")]
    [InlineData(0u, "")]
    [InlineData(99u, "")]
    public void MapBus_ReturnsExpected(uint input, string expected)
        => Assert.Equal(expected, InvokeMapBus(input));

    [Fact]
    public void FixedDrive_Record_HoldsValues()
    {
        var d = new FixedDriveService.FixedDrive("C:", "System", "NTFS", 500, 200, "SSD", "NVMe");
        Assert.Equal("C:", d.Letter);
        Assert.Equal("System", d.Label);
        Assert.Equal("NTFS", d.FileSystem);
        Assert.Equal(500, d.SizeGB);
        Assert.Equal(200, d.FreeGB);
        Assert.Equal("SSD", d.MediaType);
        Assert.Equal("NVMe", d.BusType);
    }

    [Fact]
    public void FixedDrive_Records_EquateByValue()
    {
        var a = new FixedDriveService.FixedDrive("C:", "Sys", "NTFS", 500, 200, "SSD", "NVMe");
        var b = new FixedDriveService.FixedDrive("C:", "Sys", "NTFS", 500, 200, "SSD", "NVMe");
        Assert.Equal(a, b);
    }

    [Fact]
    public void FixedDrive_WithExpression_CreatesModifiedCopy()
    {
        var a = new FixedDriveService.FixedDrive("C:", "Sys", "NTFS", 500, 200, "", "");
        var b = a with { MediaType = "SSD", BusType = "NVMe" };
        Assert.Equal("SSD", b.MediaType);
        Assert.Equal("NVMe", b.BusType);
        Assert.Equal("", a.MediaType); // original unchanged
    }
}
