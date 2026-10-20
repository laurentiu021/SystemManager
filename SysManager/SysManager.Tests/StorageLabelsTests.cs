// SysManager · StorageLabelsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The one name for each storage bus and media type. System Health's three disk lists had a table each, and a RAID or
/// SD disk was named three ways on one page.
/// </summary>
public class StorageLabelsTests
{
    [Theory]
    [InlineData(1u, "SCSI")]
    [InlineData(2u, "ATAPI")]
    [InlineData(3u, "ATA")]
    [InlineData(4u, "1394")]
    [InlineData(5u, "SSA")]
    [InlineData(6u, "Fibre")]
    [InlineData(7u, "USB")]
    [InlineData(8u, "RAID")]
    [InlineData(9u, "iSCSI")]
    [InlineData(10u, "SAS")]
    [InlineData(11u, "SATA")]
    [InlineData(12u, "SD")]
    [InlineData(13u, "MMC")]
    [InlineData(15u, "Virtual")]
    [InlineData(16u, "Storage Spaces")]
    [InlineData(17u, "NVMe")]
    public void EachKnownBusType_HasItsName(uint value, string name)
        => Assert.Equal(name, StorageLabels.Bus(value));

    [Theory]
    [InlineData(3u, "HDD")]
    [InlineData(4u, "SSD")]
    [InlineData(5u, "SCM")]
    public void EachKnownMediaType_HasItsName(uint value, string name)
        => Assert.Equal(name, StorageLabels.Media(value));

    [Theory]
    [InlineData(0u)]
    [InlineData(14u)]
    [InlineData(18u)]
    [InlineData(99u)]
    public void AValueWithNoName_ReadsAsTheListsWordForIt(uint value)
    {
        Assert.Equal(StorageLabels.OtherBus, StorageLabels.Bus(value));
        Assert.Equal(StorageLabels.UnspecifiedMedia, StorageLabels.Media(value));
        // The chkdsk list asks for an empty label instead, because it hides an empty one.
        Assert.Equal("", StorageLabels.Bus(value, unknown: ""));
        Assert.Equal("", StorageLabels.Media(value, unknown: ""));
    }

    [Fact]
    public void NoOtherSourceFile_KeepsABusTableOfItsOwn()
    {
        // Three tables drifted apart once; a fourth would start the same way. Every bus table maps NVMe, so a file
        // with that arm holds one.
        const string arm = "=> \"NVMe\"";
        var app = TestPaths.AppProject();
        var sources = Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(sources.Count > 100, $"Only {sources.Count} source files were found under {app}, so nothing was checked.");

        var own = Path.Combine(app, "Helpers", "StorageLabels.cs");
        Assert.Contains(arm, File.ReadAllText(own), StringComparison.Ordinal);

        var others = sources
            .Where(f => !string.Equals(f, own, StringComparison.OrdinalIgnoreCase)
                        && File.ReadAllText(f).Contains(arm, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(app, f))
            .ToList();
        Assert.True(others.Count == 0, "These files map bus types themselves; use StorageLabels.Bus: " + string.Join(", ", others));
    }
}
