// SysManager · DiskTreemapTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// <see cref="DiskTreemap"/> turns the Disk Analyzer's entries into the blocks of its map (#1592).
/// </summary>
/// <remarks>
/// The blocks are plain records, so what the map draws is read back without rendering. Every entry is constructed;
/// nothing here scans a disk.
/// </remarks>
public sealed class DiskTreemapTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly Color Indigo = Color.FromRgb(0x63, 0x66, 0xF1);

    private static DiskUsageEntry Folder(string name, double gb, bool denied = false) => new()
    {
        Name = name,
        FullPath = @"C:\Users\Ana\" + name,
        SizeBytes = (long)(gb * Gb),
        IsAccessDenied = denied,
    };

    private static DiskUsageEntry LooseFiles(double gb) => new()
    {
        Name = DiskAnalyzerService.LooseFilesName,
        FullPath = @"C:\Users\Ana",
        SizeBytes = (long)(gb * Gb),
        FileCount = 41,
    };

    /// <summary>The mockup's folder: six that can be labelled at 940 by 240, and a tail that cannot.</summary>
    private static List<DiskUsageEntry> Profile() =>
    [
        Folder("AppData", 38), Folder("Videos", 22), Folder("Downloads", 14), Folder("Documents", 6),
        Folder("Pictures", 4.5), Folder("OneDrive", 3), Folder("Music", 1.2), Folder("Contacts", 0.01),
        Folder("Links", 0.001), LooseFiles(0.3),
    ];

    private static IReadOnlyList<TreemapTile> Build(IReadOnlyList<DiskUsageEntry> entries, double width = 940, double height = 240)
        => DiskTreemap.Build(entries, width, height, Indigo);

    [Fact]
    public void OneFolder_FillsTheMap_WithItsName()
    {
        var tile = Assert.Single(Build([Folder("AppData", 38)]));

        Assert.Equal((0d, 0d, 940d, 240d), (tile.X, tile.Y, tile.Width, tile.Height));
        Assert.Equal("AppData", tile.Name);
        Assert.True(tile.ShowName && tile.ShowDetail);
        Assert.False(tile.IsOther);
    }

    [Fact]
    public void EveryFolderWithABlock_HasRoomForItsName_AndTheRestIsOther()
    {
        var tiles = Build(Profile());

        var folders = tiles.Where(t => !t.IsOther).ToList();
        Assert.True(folders.Count >= 3, $"only {folders.Count} folders got a block of their own");
        Assert.All(folders, t => Assert.True(t.Width >= DiskTreemap.MinBlockWidth && t.Height >= DiskTreemap.MinBlockHeight,
            $"{t.Name} got {t.Width:F0} by {t.Height:F0}"));

        var other = Assert.Single(tiles, t => t.IsOther);
        Assert.Contains("small folders and the files here", other.ToolTipText);
        Assert.DoesNotContain(tiles, t => t.Name is "Contacts" or "Links");
    }

    [Fact]
    public void TheBlocks_TileTheMap_AndKeepTheirShares()
    {
        var entries = Profile();
        var tiles = Build(entries);

        Assert.Equal(940d * 240, tiles.Sum(t => t.Width * t.Height), 3);
        var total = (double)entries.Sum(e => e.SizeBytes);
        foreach (var tile in tiles.Where(t => !t.IsOther))
            Assert.Equal(tile.Entry!.SizeBytes / total * 940 * 240, tile.Width * tile.Height, 3);
    }

    [Fact]
    public void AFolderWithEnoughShareButNoRoomForItsName_JoinsOther()
    {
        // Each small folder's share is more than one name's worth of area, but beside a folder holding 96% of the
        // space they would be 38 pixels wide: slivers. So they go into "Other" instead.
        List<DiskUsageEntry> entries = [Folder("Videos", 96), .. Enumerable.Range(0, 4).Select(i => Folder("f" + i, 1))];

        var tiles = Build(entries);

        Assert.Equal(new[] { "Videos", "Other" }, tiles.Select(t => t.Name));
        Assert.Contains("4 small folders", tiles[1].ToolTipText);
    }

    [Fact]
    public void AShare_RoundsHalfUp()
    {
        // 1 of 8 is 12.5%, which reads 13% as the list's bars are read, never 12%.
        var tiles = Build([Folder("Videos", 7), Folder("Music", 1)]);

        Assert.Equal("1.0 GB · 13%", tiles.Single(t => t.Name == "Music").Detail);
    }

    [Fact]
    public void AWiderMap_GivesMoreFoldersABlockOfTheirOwn()
    {
        // Two hundred equal folders: at 420 by 240 each has 504 square pixels, under the 960 a name needs, so all of
        // them are "Other"; at 1800 by 240 each has 2160, and many get a block of their own.
        List<DiskUsageEntry> entries = [.. Enumerable.Range(0, 200).Select(i => Folder("f" + i, 1))];

        var narrow = Build(entries, width: 420).Count(t => !t.IsOther);
        var wide = Build(entries, width: 1800).Count(t => !t.IsOther);

        Assert.Equal(0, narrow);
        Assert.True(wide > 0, $"{wide} folders at 1800 px");
    }

    [Fact]
    public void TheBlocks_AreLargestFirst_WhichIsTheOrderTabMovesThroughThem()
    {
        var sizes = Build(Profile()).Select(t => t.Width * t.Height).ToList();

        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [Fact]
    public void ABlock_SaysItsNameSizeAndShare()
    {
        var appData = Build(Profile()).First();

        Assert.Equal("AppData", appData.Name);
        Assert.Equal("38.0 GB · 43%", appData.Detail);
        Assert.Equal("AppData, 38.0 GB, 43 percent", appData.SpokenName);
        Assert.Equal("AppData · 38.0 GB · 43% of this folder\nClick to open · right-click to show in Explorer", appData.ToolTipText);
        Assert.Equal(@"C:\Users\Ana\AppData", appData.Entry!.FullPath);
    }

    [Fact]
    public void AFolderThatCouldNotAllBeRead_SaysSo_InTheBlock()
    {
        var tile = Assert.Single(Build([Folder("AppData", 38, denied: true)]));

        Assert.True(tile.IsPartlyUnreadable);
        Assert.EndsWith("Part of this folder could not be read, so it may be using more space than shown.", tile.ToolTipText);
        Assert.Equal("AppData, 38.0 GB, 100 percent, partly unreadable", tile.SpokenName);
    }

    [Fact]
    public void TheLargestBlock_IsTheAccent_AndSmallerOnesAreLighter()
    {
        var folders = Build(Profile()).Where(t => !t.IsOther).ToList();

        Assert.Equal("#6366F1", folders[0].FillHex);
        var lightness = folders.Select(t => Luminance(Parse(t.FillHex!))).ToList();
        Assert.Equal(lightness.Order(), lightness);
        Assert.True(lightness[^1] > lightness[0]);
    }

    [Fact]
    public void EveryBlocksText_ReadsOnItsFill_OnEveryPreset()
    {
        // The accent runs from indigo to amber across the twelve presets, so no one text colour reads on all of them.
        var checkedBlocks = 0;
        foreach (var (id, preset) in ThemePreset.Defaults)
        {
            foreach (var tile in DiskTreemap.Build(Profile(), 1800, 240, preset.Accent).Where(t => !t.IsOther))
            {
                checkedBlocks++;
                var ratio = Contrast(Parse(tile.TextHex!), Parse(tile.FillHex!));
                Assert.True(ratio >= 4.5, $"{id}: {tile.Name} is {ratio:F2} to 1 on {tile.FillHex}");
            }
        }

        // Vacuity floor: twelve presets with several folder blocks each.
        Assert.True(checkedBlocks >= 12 * 3, $"only {checkedBlocks} blocks were checked");
    }

    [Fact]
    public void TheOtherBlock_TakesTheThemesGrey_AndOpensNothing()
    {
        var other = Assert.Single(Build(Profile()), t => t.IsOther);

        Assert.Null(other.Entry);
        Assert.Null(other.FillHex);
        Assert.Null(other.TextHex);
        Assert.Equal("Other", other.Name);
        Assert.StartsWith("Other, ", other.SpokenName);
    }

    [Theory]
    [InlineData(1, false, "1 small folder")]
    [InlineData(3, false, "3 small folders")]
    [InlineData(3, true, "3 small folders and the files here")]
    [InlineData(0, true, "the files here")]
    public void TheOtherBlock_SaysWhatItHolds(int folders, bool files, string expected)
    {
        List<DiskUsageEntry> merged = [.. Enumerable.Range(0, folders).Select(i => Folder("f" + i, 0.1))];
        if (files) merged.Add(LooseFiles(0.1));

        Assert.Equal(expected, DiskTreemap.DescribeMerged(merged));
    }

    [Fact]
    public void AMapTooSmallForAnyName_IsOneOtherBlock()
    {
        // Ten equal folders in 60 by 30: none can have the 40 by 24 its name needs.
        List<DiskUsageEntry> entries = [.. Enumerable.Range(0, 10).Select(i => Folder("f" + i, 1))];

        var tile = Assert.Single(Build(entries, width: 60, height: 30));

        Assert.True(tile.IsOther);
        Assert.Contains("10 small folders", tile.ToolTipText);
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(39, 240)]
    [InlineData(940, 23)]
    [InlineData(double.NaN, 240)]
    [InlineData(940, double.PositiveInfinity)]
    public void BeforeTheMapHasRoomForOneName_ThereAreNoBlocks(double width, double height)
        => Assert.Empty(Build(Profile(), width, height));

    [Fact]
    public void NoFolderWithSpace_DrawsNoMap()
    {
        // Loose files cannot be opened, and a map of one grey block would answer nothing.
        Assert.Empty(Build([LooseFiles(3)]));
        Assert.Empty(Build([Folder("Empty", 0), LooseFiles(3)]));
        Assert.Empty(Build([]));
    }

    [Fact]
    public void TheMap_FollowsItsEntriesAndItsSize()
    {
        using var map = new DiskTreemap();
        Assert.False(map.HasMap);

        map.Update(Profile());
        Assert.True(map.HasMap);
        Assert.Empty(map.Tiles);   // not laid out yet

        map.Width = 940;
        map.Height = 240;
        Assert.Equal(940d * 240, map.Tiles.Sum(t => t.Width * t.Height), 3);

        // Laid out again for the new size, not stretched.
        map.Width = 1800;
        Assert.Equal(1800d * 240, map.Tiles.Sum(t => t.Width * t.Height), 3);

        map.Update([LooseFiles(3)]);
        Assert.False(map.HasMap);
        Assert.Empty(map.Tiles);
    }

    [Fact]
    public void ADisposedMap_IgnoresUpdates_AndCanBeDisposedTwice()
    {
        var map = new DiskTreemap { Width = 940, Height = 240 };
        map.Dispose();

        map.Update(Profile());
        map.Dispose();

        Assert.False(map.HasMap);
        Assert.Empty(map.Tiles);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
