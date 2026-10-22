// SysManager · FixedDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FixedDriveService"/>: the drive record. The bus and media names are
/// <see cref="SysManager.Helpers.StorageLabels"/>'s, tested there; the WMI enumeration is an integration test.
/// </summary>
public class FixedDriveServiceTests
{
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

    // ---------- #2802: the sizes are carried as read ----------

    private const long Gib = 1L << 30;

    private static long Bytes(double gib) => (long)(gib * Gib);

    [Fact]
    public void Describe_KeepsTheSizesAsRead_AndNamesTheDrive()
    {
        var drive = FixedDriveService.Describe(@"C:\", " ", "NTFS", Bytes(40), Bytes(9.7));

        Assert.Equal("C:", drive.Letter);
        Assert.Equal("C:", drive.Label);      // a blank label falls back to the letter
        Assert.Equal("NTFS", drive.FileSystem);
        Assert.Equal(40, drive.SizeGB, 6);
        Assert.Equal(9.7, drive.FreeGB, 6);
    }

    /// <summary>
    /// The Health Score's floors see the free space a drive has, not the whole gigabytes it rounds to (#2802).
    /// </summary>
    /// <remarks>
    /// Rounded, 9.7 GB free read as 10 and escaped the floor under 10, and 19.7 read as 20 and escaped the one under
    /// 20. On the drives below each percentage looks healthy, so the floor is the whole verdict.
    /// </remarks>
    [Theory]
    [InlineData(40, 9.7, 10)]     // 24% free: under 10 GB is the bottom band, not the 55 a rounded 10 got
    [InlineData(100, 19.7, 55)]   // 19.7% free: under 20 GB caps at 55; a rounded 20 read as 20% free and scored 100
    public void TheFreeSpaceFloors_SeeTheSizesAsRead(double sizeGib, double freeGib, int expected)
    {
        var drive = FixedDriveService.Describe(@"C:\", "Windows", "NTFS", Bytes(sizeGib), Bytes(freeGib));

        Assert.Equal(expected, HealthScoreService.ComputeFreeSpaceScore([drive], "C:"));
    }

    [Fact]
    public void ADriveJustUnder20GBFree_IsToldToFreeSpace()
    {
        var drive = FixedDriveService.Describe(@"C:\", "Windows", "NTFS", Bytes(100), Bytes(19.7));

        var result = HealthScoreService.Evaluate(null, null, null, [drive], "C:");

        Assert.Contains(result.Recommendations, r => r.NavTargetId == "nav-deep-cleanup");
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
