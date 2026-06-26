// SysManager · DisplayMode
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>A selectable display mode: resolution + refresh rate.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    /// <summary>e.g. "2560 × 1440 @ 165 Hz".</summary>
    public string Display => $"{Width} × {Height} @ {RefreshHz} Hz";

    /// <summary>e.g. "2560×1440".</summary>
    public string ResolutionDisplay => $"{Width}×{Height}";

    public string RefreshDisplay => $"{RefreshHz} Hz";
}

/// <summary>An attached display adapter and its current/available modes.</summary>
public sealed record DisplayDevice(string DeviceName, string FriendlyName, bool IsPrimary, bool IsActive)
{
    public string Display => IsPrimary ? $"{FriendlyName} (primary)" : FriendlyName;
}
