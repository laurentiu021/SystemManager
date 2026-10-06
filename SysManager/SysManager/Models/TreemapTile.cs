// SysManager · TreemapTile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One block of the Disk Analyzer's map (#1592): a folder sized by the space it uses, or the grey block that holds
/// everything too small to label.
/// </summary>
/// <param name="Entry">The folder the block opens, or null for the merged block, which opens nothing.</param>
/// <param name="X">Left edge, in the map's own pixels.</param>
/// <param name="Y">Top edge, in the map's own pixels.</param>
/// <param name="Width">Width, in the map's own pixels.</param>
/// <param name="Height">Height, in the map's own pixels.</param>
/// <param name="Name">The folder's name, or "Other".</param>
/// <param name="Detail">Size and share, e.g. "38.0 GB · 42%".</param>
/// <param name="ShowName">Whether the block is large enough for its name.</param>
/// <param name="ShowDetail">Whether the block is tall enough for the second line as well.</param>
/// <param name="FillHex">The block's colour as "#RRGGBB"; null for the merged block, which takes the theme's grey.</param>
/// <param name="TextHex">The text colour that reads on <paramref name="FillHex"/>; null for the merged block.</param>
/// <param name="ToolTipText">What hovering shows.</param>
/// <param name="SpokenName">What a screen reader says, e.g. "AppData, 38.0 GB, 42 percent".</param>
/// <param name="IsPartlyUnreadable">Part of the folder could not be read, so it may be larger than shown.</param>
public sealed record TreemapTile(
    DiskUsageEntry? Entry,
    double X,
    double Y,
    double Width,
    double Height,
    string Name,
    string Detail,
    bool ShowName,
    bool ShowDetail,
    string? FillHex,
    string? TextHex,
    string ToolTipText,
    string SpokenName,
    bool IsPartlyUnreadable)
{
    /// <summary>True for the merged block of small folders and loose files.</summary>
    public bool IsOther => Entry is null;
}
