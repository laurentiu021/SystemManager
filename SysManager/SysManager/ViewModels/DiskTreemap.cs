// SysManager · DiskTreemap
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// The Disk Analyzer's map: the folder being looked at, drawn as blocks sized by the space each subfolder uses
/// (#1592).
/// </summary>
/// <remarks>
/// <para>One level only, the folder the list shows, so the map and the list always hold the same things and Up works
/// for both. Nothing new is measured: the blocks are the list's own entries.</para>
/// <para>Every folder big enough for its name gets its own block; the rest, and the files loose in the folder, merge
/// into one grey "Other" block. Big enough is decided on the real pixel size, so a wider window shows more blocks:
/// the view reports its size through <see cref="SizeObserver"/> and the layout is redone on every change.</para>
/// <para>One hue, the theme's accent, lighter for smaller blocks, so the colours work on every preset and do not
/// suggest categories. The text colour is picked per block by <see cref="ThemeService.OnColor"/>, the rule the
/// theme uses for anything drawn on the accent, and the blocks are recoloured when the theme changes.</para>
/// </remarks>
public sealed partial class DiskTreemap : ObservableObject, IDisposable
{
    /// <summary>The smallest block a folder's name fits in; anything smaller merges into "Other".</summary>
    internal const double MinBlockWidth = 40;

    /// <summary>See <see cref="MinBlockWidth"/>.</summary>
    internal const double MinBlockHeight = 24;

    /// <summary>The height a block needs for the size line under the name.</summary>
    internal const double MinDetailHeight = 44;

    /// <summary>How far toward white the smallest folder's block is taken; the largest is the accent itself.</summary>
    internal const double LightestShade = 0.45;

    private IReadOnlyList<DiskUsageEntry> _entries = [];
    private bool _disposed;

    /// <summary>The blocks, largest first, which is also the order Tab moves through them.</summary>
    public BulkObservableCollection<TreemapTile> Tiles { get; } = new();

    /// <summary>The map's width in pixels, as the view laid it out.</summary>
    [ObservableProperty] private double _width;

    /// <summary>The map's height in pixels, as the view laid it out.</summary>
    [ObservableProperty] private double _height;

    /// <summary>True when there is a subfolder with space to draw. Otherwise the list's own empty state speaks.</summary>
    [ObservableProperty] private bool _hasMap;

    public DiskTreemap() => ThemeService.Instance.ThemeChanged += Relayout;

    /// <summary>Redraws from <paramref name="entries"/>, the list's own entries, in any order.</summary>
    public void Update(IReadOnlyList<DiskUsageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (_disposed) return;

        _entries = [.. entries];
        HasMap = _entries.Any(e => !IsLooseFiles(e) && e.SizeBytes > 0);
        Relayout();
    }

    partial void OnWidthChanged(double value) => Relayout();

    partial void OnHeightChanged(double value) => Relayout();

    private void Relayout()
    {
        if (_disposed) return;
        Tiles.ReplaceWith(HasMap ? Build(_entries, Width, Height, ThemeService.Instance.CurrentTheme.Accent) : []);
    }

    /// <summary>
    /// The blocks for <paramref name="entries"/> in a map of <paramref name="width"/> by <paramref name="height"/>,
    /// largest first. Empty when the map has no room for even one labelled block, which is also its state before the
    /// view has been laid out.
    /// </summary>
    internal static IReadOnlyList<TreemapTile> Build(
        IReadOnlyList<DiskUsageEntry> entries, double width, double height, Color accent)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!(width >= MinBlockWidth) || !(height >= MinBlockHeight)) return [];

        List<DiskUsageEntry> folders = [.. entries.Where(e => !IsLooseFiles(e) && e.SizeBytes > 0)
                                                  .OrderByDescending(e => e.SizeBytes)];
        List<DiskUsageEntry> loose = [.. entries.Where(e => IsLooseFiles(e) && e.SizeBytes > 0)];
        if (folders.Count == 0) return [];

        var bounds = new Rect(0, 0, width, height);
        var total = (double)(folders.Sum(e => e.SizeBytes) + loose.Sum(e => e.SizeBytes));

        // A block's area is its share of the map, so a folder whose share is under one name's worth of area can never
        // be labelled, whatever the layout. That bounds the search, and a folder with thousands of children costs a
        // handful of layouts rather than thousands.
        var possible = folders.TakeWhile(f => f.SizeBytes / total * width * height >= MinBlockWidth * MinBlockHeight).Count();

        for (var shown = possible; shown > 0; shown--)
        {
            var blocks = Blocks(folders, loose, shown);
            var rects = TreemapLayout.Squarify([.. blocks.Select(b => (double)b.Size)], bounds);
            var labelled = true;
            for (var i = 0; i < blocks.Count && labelled; i++)
                labelled = blocks[i].Entry is null || (rects[i].Width >= MinBlockWidth && rects[i].Height >= MinBlockHeight);
            if (labelled) return ToTiles(blocks, rects, total, shown, accent);
        }

        // Not even the largest folder fits its name: everything is "Other".
        var all = Blocks(folders, loose, 0);
        return ToTiles(all, TreemapLayout.Squarify([.. all.Select(b => (double)b.Size)], bounds), total, 0, accent);
    }

    /// <summary>
    /// The <paramref name="shown"/> largest folders as their own blocks, and the rest with the loose files as one,
    /// all ordered largest first so the layout stays square.
    /// </summary>
    private static List<Block> Blocks(List<DiskUsageEntry> folders, List<DiskUsageEntry> loose, int shown)
    {
        List<Block> blocks = [.. folders.Take(shown).Select(f => new Block(f, f.SizeBytes, []))];
        List<DiskUsageEntry> merged = [.. folders.Skip(shown), .. loose];
        if (merged.Count > 0) blocks.Add(new Block(null, merged.Sum(e => e.SizeBytes), merged));
        blocks.Sort((a, b) => b.Size.CompareTo(a.Size));
        return blocks;
    }

    private static List<TreemapTile> ToTiles(List<Block> blocks, Rect[] rects, double total, int shown, Color accent)
    {
        List<TreemapTile> tiles = [];
        var rank = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var rect = rects[i];
            var showName = rect.Width >= MinBlockWidth && rect.Height >= MinBlockHeight;
            var showDetail = showName && rect.Height >= MinDetailHeight;
            var share = Share(block.Size, total);

            if (block.Entry is { } folder)
            {
                var shade = shown <= 1 ? 0 : LightestShade * rank / (shown - 1);
                rank++;
                var fill = ThemeService.Lighten(accent, shade);
                var unreadable = folder.IsAccessDenied
                    ? "\nPart of this folder could not be read, so it may be using more space than shown."
                    : "";
                tiles.Add(new TreemapTile(
                    folder, rect.X, rect.Y, rect.Width, rect.Height,
                    folder.Name, $"{folder.SizeDisplay} · {share}%", showName, showDetail,
                    Hex(fill), Hex(ThemeService.OnColor(fill)),
                    $"{folder.Name} · {folder.SizeDisplay} · {share}% of this folder\nClick to open · right-click to show in Explorer{unreadable}",
                    $"{folder.Name}, {folder.SizeDisplay}, {share} percent{(folder.IsAccessDenied ? ", partly unreadable" : "")}",
                    folder.IsAccessDenied));
            }
            else
            {
                var size = FormatHelper.FormatSize(block.Size);
                var holds = DescribeMerged(block.Merged);
                tiles.Add(new TreemapTile(
                    null, rect.X, rect.Y, rect.Width, rect.Height,
                    "Other", $"{size} · {share}%", showName, showDetail,
                    null, null,
                    $"Other — {holds}, {size}",
                    $"Other, {holds}, {size}",
                    block.Merged.Any(e => e.IsAccessDenied)));
            }
        }
        return tiles;
    }

    /// <summary>What the "Other" block holds, in words: "3 small folders and the files here".</summary>
    internal static string DescribeMerged(IReadOnlyList<DiskUsageEntry> merged)
    {
        var folders = merged.Count(e => !IsLooseFiles(e));
        var files = merged.Any(IsLooseFiles);
        var small = folders == 1 ? "1 small folder" : $"{folders.ToString(CultureInfo.InvariantCulture)} small folders";
        return (folders, files) switch
        {
            (0, _) => "the files here",
            (_, true) => small + " and the files here",
            _ => small,
        };
    }

    /// <summary>A share of the folder as a whole percent, the way the list's bars are read.</summary>
    private static string Share(long size, double total) =>
        Math.Round(size * 100 / total, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture);

    private static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    private static bool IsLooseFiles(DiskUsageEntry e) => e.Name == DiskAnalyzerService.LooseFilesName;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.Instance.ThemeChanged -= Relayout;
    }

    /// <summary>One block before layout: a folder, or the merged rest when <see cref="Entry"/> is null.</summary>
    private sealed record Block(DiskUsageEntry? Entry, long Size, IReadOnlyList<DiskUsageEntry> Merged);
}
