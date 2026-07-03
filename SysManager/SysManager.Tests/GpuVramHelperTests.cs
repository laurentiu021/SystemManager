// SysManager · GpuVramHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Pins <see cref="GpuVramHelper.SelectVramBytes"/> — the pure VRAM-source selection that both
/// the System Report and the About-page diagnostics share (F27). <c>Win32_VideoController.AdapterRAM</c>
/// is a uint32 that saturates near 4 GiB, so the 64-bit driver <c>qwMemorySize</c> must win when
/// present; AdapterRAM is only a fallback. The registry-read path itself needs a live driver key
/// and is not unit-tested.
/// </summary>
public class GpuVramHelperTests
{
    [Fact]
    public void SelectVramBytes_PrefersRegistryQwordOverAdapterRam()
    {
        // Regression (F27): an 8 GB card reports ~4 GB via AdapterRAM's uint32 ceiling. The
        // 64-bit qwMemorySize from the driver registry key must be reported instead.
        const ulong eightGB = 8UL * 1024 * 1024 * 1024;
        const ulong adapterRamCap = 4290772992UL; // AdapterRAM's ~4 GiB uint32 ceiling

        var chosen = GpuVramHelper.SelectVramBytes(qwMemorySize: eightGB, adapterRam: adapterRamCap);

        Assert.Equal(eightGB, chosen);
    }

    [Fact]
    public void SelectVramBytes_FallsBackToAdapterRam_WhenRegistryMissing()
    {
        const ulong twoGB = 2UL * 1024 * 1024 * 1024;

        Assert.Equal(twoGB, GpuVramHelper.SelectVramBytes(qwMemorySize: null, adapterRam: twoGB));
        Assert.Equal(twoGB, GpuVramHelper.SelectVramBytes(qwMemorySize: 0UL, adapterRam: twoGB));
    }

    [Fact]
    public void SelectVramBytes_ReturnsNull_WhenNeitherUsable()
    {
        Assert.Null(GpuVramHelper.SelectVramBytes(qwMemorySize: null, adapterRam: null));
        Assert.Null(GpuVramHelper.SelectVramBytes(qwMemorySize: 0UL, adapterRam: 0UL));
    }
}
