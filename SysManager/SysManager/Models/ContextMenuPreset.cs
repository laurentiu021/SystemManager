// SysManager · ContextMenuPreset — preset definitions for context menu configurations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Represents a context menu preset that defines the menu style.
/// Win10 = classic full menu, Win11 = modern compact menu.
/// Custom = whichever style is active + user's manual entry toggles.
/// </summary>
public sealed record ContextMenuPreset(
    string Id,
    string Name,
    string Description,
    bool ForcesClassicMenu)
{
    /// <summary>
    /// All available presets, keyed by ID for quick lookup.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ContextMenuPreset> All = new Dictionary<string, ContextMenuPreset>
    {
        ["win10"] = new("win10",
            "Win10 Default",
            "Forces the classic full context menu — all entries visible directly on right-click. No \"Show more options\" needed.",
            ForcesClassicMenu: true),

        ["win11"] = new("win11",
            "Win11 Default",
            "Restores the modern Windows 11 compact context menu with \"Show more options\" at the bottom.",
            ForcesClassicMenu: false),

        ["custom"] = new("custom",
            "Custom",
            "Uses the currently active menu style (Win10 or Win11) plus your manual entry toggles below.",
            ForcesClassicMenu: false),
    };
}
