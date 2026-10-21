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

    /// <summary>
    /// How many pixels the mode has, which the Resolution column sorts on: sorting <see cref="ResolutionDisplay"/> put
    /// "2560×1440" before "800×600" (#2819).
    /// </summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// The same mode when the resolution and the refresh rate match. The bit depth is left out on purpose: Apply sets
    /// only those three (<c>DM_PELSWIDTH</c>, <c>DM_PELSHEIGHT</c>, <c>DM_DISPLAYFREQUENCY</c>), and the mode list keeps
    /// whichever depth Windows listed first, so comparing depths made Apply look available for the mode in use.
    /// </summary>
    public bool Equals(DisplayMode? other) =>
        other is not null && Width == other.Width && Height == other.Height && RefreshHz == other.RefreshHz;

    public override int GetHashCode() => HashCode.Combine(Width, Height, RefreshHz);
}

/// <summary>An attached display adapter and its current/available modes.</summary>
public sealed record DisplayDevice(string DeviceName, string FriendlyName, bool IsPrimary, bool IsActive)
{
    public string Display => IsPrimary ? $"{FriendlyName} (primary)" : FriendlyName;
}
