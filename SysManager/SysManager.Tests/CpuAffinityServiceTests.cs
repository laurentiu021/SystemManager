// SysManager · CpuAffinityServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

public class CpuAffinityServiceTests
{
    [Theory]
    [InlineData(1, 0b1L)]
    [InlineData(2, 0b11L)]
    [InlineData(4, 0b1111L)]
    [InlineData(8, 0xFFL)]
    public void AllCoresMask_SetsLowBits(int count, long expected)
        => Assert.Equal(expected, CpuAffinityService.AllCoresMask(count));

    [Fact]
    public void AllCoresMask_64_IsAllBits()
        => Assert.Equal(-1L, CpuAffinityService.AllCoresMask(64));

    [Fact]
    public void MaskFromIndices_BuildsBitmask()
    {
        Assert.Equal(0b1011L, CpuAffinityService.MaskFromIndices([0, 1, 3]));
        Assert.Equal(0L, CpuAffinityService.MaskFromIndices([]));
    }

    [Fact]
    public void MaskFromIndices_IgnoresOutOfRangeIndices()
    {
        // Negative and >=64 are dropped, not crash.
        Assert.Equal(0b1L, CpuAffinityService.MaskFromIndices([0, -1, 64, 99]));
    }

    [Theory]
    [InlineData(0b1010L, 1, true)]
    [InlineData(0b1010L, 0, false)]
    [InlineData(0b1010L, 3, true)]
    [InlineData(0b1010L, 2, false)]
    public void IsCoreInMask_ChecksBit(long mask, int index, bool expected)
        => Assert.Equal(expected, CpuAffinityService.IsCoreInMask(mask, index));

    [Fact]
    public void IsCoreInMask_OutOfRange_IsFalse()
    {
        Assert.False(CpuAffinityService.IsCoreInMask(-1L, -1));
        Assert.False(CpuAffinityService.IsCoreInMask(-1L, 64));
    }

    [Fact]
    public void RoundTrip_IndicesToMaskToCheck()
    {
        long mask = CpuAffinityService.MaskFromIndices([2, 5, 7]);
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 2));
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 5));
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 7));
        Assert.False(CpuAffinityService.IsCoreInMask(mask, 3));
    }
}
