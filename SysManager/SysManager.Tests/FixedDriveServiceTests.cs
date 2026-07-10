// SysManager · FixedDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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

    // ---------- P2 #33 regression: structural verification ----------

    [Fact]
    public void Enumerate_NeverThrows()
    {
        // P2 #33: DriveFormat was previously read in a LINQ .Where() predicate (lazy
        // evaluation) that ran during foreach MoveNext — OUTSIDE the per-drive try/catch.
        // An IOException from a BitLocker-locked volume would abort enumeration of ALL
        // remaining drives. After the fix, DriveFormat is read inside the try block, so
        // one flaky drive cannot poison the entire result. This test verifies the method
        // degrades gracefully (returns what it can) rather than throwing.
        var ex = Record.Exception(() => FixedDriveService.Enumerate());
        Assert.Null(ex);
    }

    // Regression: WMI returns DBNull.Value (not null) for absent properties, and
    // Convert.ToUInt32(DBNull.Value) throws — which crashed drive enumeration.
    [Fact]
    public void ToUInt32Safe_DBNull_ReturnsZero()
        => Assert.Equal(0u, FixedDriveService.ToUInt32Safe(DBNull.Value));

    [Fact]
    public void ToUInt32Safe_Null_ReturnsZero()
        => Assert.Equal(0u, FixedDriveService.ToUInt32Safe(null));

    [Theory]
    [InlineData(4, 4u)]
    [InlineData((uint)17, 17u)]
    [InlineData("11", 11u)]
    public void ToUInt32Safe_ConvertibleValue_ReturnsValue(object input, uint expected)
        => Assert.Equal(expected, FixedDriveService.ToUInt32Safe(input));

    [Fact]
    public void ToUInt32Safe_Unconvertible_ReturnsZero()
        => Assert.Equal(0u, FixedDriveService.ToUInt32Safe("not-a-number"));
}
