// SysManager · StatusColors — single source of truth for status hex colours
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Canonical status-colour hex strings shared by the models and view-models that
/// expose a <c>*ColorHex</c> property (disk health, temperature, health score,
/// tune-up results, cleanup categories, etc.). These were previously copy-pasted as
/// bare hex literals across ~10 files; centralising them here keeps the palette from
/// drifting between copies. The values are byte-identical to the previous literals.
/// </summary>
internal static class StatusColors
{
    /// <summary>Good / healthy / safe — green.</summary>
    public const string Good = "#22C55E";

    /// <summary>Caution / warning — amber.</summary>
    public const string Warning = "#F59E0B";

    /// <summary>Informational / nominal — blue.</summary>
    public const string Info = "#3B82F6";

    /// <summary>Elevated concern — light red.</summary>
    public const string Elevated = "#F87171";

    /// <summary>Bad / critical / failing — red.</summary>
    public const string Bad = "#EF4444";

    /// <summary>Unknown / no data / neutral — grey.</summary>
    public const string Neutral = "#9AA0A6";
}
