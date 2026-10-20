// SysManager · CleanupPreScanServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Quick Cleanup's Recycle Bin label comes from the shell's count of the bin, not from a walk of its folders.
/// </summary>
/// <remarks>
/// Only the bin's label is asserted here. The temp label walks the real temp folders, which is what made the whole
/// measurement a seam in the first place, and the bin is a substitute, so nothing here reads the PC's own bin.
/// </remarks>
public sealed class CleanupPreScanServiceTests
{
    private static string BinLabel(RecycleBinContents? holds)
    {
        var bin = Substitute.For<IRecycleBin>();
        bin.Query().Returns(holds);
        return new CleanupPreScanService(bin).MeasureRecycleBin();
    }

    [Fact]
    public void AnEmptyBin_ReadsEmpty()
    {
        // The walk counted the desktop.ini every bin folder holds, so an empty bin read "0.0 MB in Recycle Bin".
        Assert.Equal("Empty", BinLabel(new RecycleBinContents(0, 0)));
    }

    [Fact]
    public void ABinWithItems_ReadsItsSize()
        => Assert.Equal("18.0 MB in Recycle Bin", BinLabel(new RecycleBinContents(18L * 1024 * 1024, 4)));

    [Fact]
    public void ABinHoldingOnlyEmptyFiles_IsNotCalledEmpty()
        => Assert.Equal("0.0 MB in Recycle Bin", BinLabel(new RecycleBinContents(0, 2)));

    [Fact]
    public void ABinTheShellCannotRead_IsNotCalledEmpty()
        => Assert.Equal("Unable to scan", BinLabel(holds: null));
}
