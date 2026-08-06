// SysManager · StatusColors — single source of truth for semantic status colour keys
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Canonical status-colour KEYS shared by the models and view-models that expose a
/// <c>*ColorHex</c> property (disk health, temperature, health score, tune-up results,
/// cleanup categories, etc.).
/// <para>These used to be dark-calibrated hex literals (<c>#22C55E</c> and friends). A
/// <c>const</c> is baked in at compile time, so <see cref="Services.ThemeService"/> could never
/// recompute them — and on the light presets they rendered as pale text on near-white cards,
/// below the AA 4.5:1 floor, on exactly the tabs that answer "is my PC OK?".
/// <see cref="Services.ThemeService"/> already recomputes the equivalent semantic brushes per
/// mode, so these now NAME that brush rather than duplicating a colour:
/// <see cref="HexToBrushConverter"/> resolves a key against the live theme resources and still
/// falls back to parsing a literal hex, so any other caller keeps working.</para>
/// <para>The producers' property names are deliberately unchanged, so none of the XAML binding
/// paths had to move.</para>
/// </summary>
internal static class StatusColors
{
    /// <summary>Good / healthy / safe — the theme's Success brush.</summary>
    public const string Good = "Success";

    /// <summary>Caution / warning — the theme's Warning brush.</summary>
    public const string Warning = "Warning";

    /// <summary>Informational / nominal — the theme's Info brush.</summary>
    public const string Info = "Info";

    /// <summary>
    /// Elevated concern — one step below <see cref="Bad"/>. Maps to the theme's Warning brush:
    /// there is no separate "elevated" brush, and amber is the honest reading of "worse than
    /// fine, not yet failing". The old value was a light red (#F87171) which on a light surface
    /// was both illegible and easy to mistake for the failure colour.
    /// </summary>
    public const string Elevated = "Warning";

    /// <summary>Bad / critical / failing — the theme's Danger brush.</summary>
    public const string Bad = "Danger";

    /// <summary>
    /// Unknown / no data / neutral — the theme's TextMuted brush, already AA-verified per preset
    /// (ThemeTextContrastTests) and the same muted tone the rest of the app uses for "nothing to
    /// report".
    /// </summary>
    public const string Neutral = "TextMuted";
}
