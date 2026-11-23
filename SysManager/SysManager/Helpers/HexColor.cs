// SysManager · HexColor — single source of truth for reading a colour from text
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Reads a colour written #RRGGBB or #AARRGGBB, and nothing else, from text the app did not write itself: the
/// Custom-mode colour boxes, and a custom theme in theme.json, which a profile import writes as the profile has it.
/// </summary>
/// <remarks>
/// WPF's <c>ColorConverter.ConvertFromString</c> read both. It also reads colour names, the short #RGB and #ARGB forms,
/// scRGB ("sc#…") and a ContextColor form whose colour-profile address it opens, wherever that address points. Six
/// digits are opaque, as WPF read them, and eight put the alpha first, as <see cref="Color.ToString()"/> writes it.
/// </remarks>
public static partial class HexColor
{
    /// <summary>
    /// The colour <paramref name="text"/> writes, with surrounding white space ignored as WPF's converter ignored it, or
    /// false and the default colour when it is anything other than #RRGGBB or #AARRGGBB. Never throws.
    /// </summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text is null) return false;
        var trimmed = text.Trim();
        if (!Pattern().IsMatch(trimmed)) return false;

        var argb = uint.Parse(trimmed.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        if (trimmed.Length == 7) argb |= 0xFF000000;
        color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return true;
    }

    // \A…\z, the anchors this codebase keeps for a pattern at a trust boundary; the Trim above has already removed a
    // trailing newline, as WPF's own trim did.
    [GeneratedRegex(@"\A#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
