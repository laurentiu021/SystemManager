// SysManager · ContextMenuPreset — preset definitions for context menu configurations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Represents a context menu preset that defines which entries should be
/// enabled and what menu style to use (classic vs modern).
/// </summary>
public sealed record ContextMenuPreset(
    string Id,
    string Name,
    string Description,
    bool ForcesClassicMenu,
    IReadOnlySet<string> EnabledEntries)
{
    private static readonly HashSet<string> AllEntries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MinimalEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "copy", "cut", "paste", "delete", "rename", "properties"
    };

    private static readonly HashSet<string> DeveloperEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "runas", "copy", "cut", "paste", "delete", "rename", "properties",
        "cmd", "powershell", "pwsh", "git_bash", "git_gui", "gitbash", "gitgui",
        "terminal", "open in terminal", "openwt", "wt",
        "vscode", "open with code", "vscode_open", "code",
        "windows terminal", "command prompt"
    };

    private static readonly HashSet<string> Win11Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "runas", "edit", "copy", "cut", "paste", "delete", "rename",
        "properties", "pintohomefile", "pintostart", "share",
        "windows.modernshare", "opennewwindow", "opennewtab",
        "terminal", "open in terminal", "openwt"
    };

    /// <summary>
    /// All available presets, keyed by ID for quick lookup.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ContextMenuPreset> All = new Dictionary<string, ContextMenuPreset>
    {
        ["win10"] = new("win10",
            "Win10 Default",
            "Forces the classic full context menu — all entries visible directly on right-click. No \"Show more options\" step needed.",
            ForcesClassicMenu: true,
            EnabledEntries: AllEntries),

        ["win11"] = new("win11",
            "Win11 Default",
            "Restores the modern Windows 11 context menu with icon bar at top and \"Show more options\" at the bottom.",
            ForcesClassicMenu: false,
            EnabledEntries: Win11Entries),

        ["minimal"] = new("minimal",
            "Minimal",
            "Clean, fast right-click menu. Only the basics — Open, Copy, Cut, Paste, Delete, Rename, Properties.",
            ForcesClassicMenu: true,
            EnabledEntries: MinimalEntries),

        ["developer"] = new("developer",
            "Developer",
            "Essentials + developer tools — Git Bash, Git GUI, Terminal, VS Code, PowerShell, Command Prompt, Run as Admin.",
            ForcesClassicMenu: true,
            EnabledEntries: DeveloperEntries),

        ["power"] = new("power",
            "Power User",
            "Everything enabled. All context menu entries — Windows built-in and third-party — all turned on.",
            ForcesClassicMenu: true,
            EnabledEntries: AllEntries),
    };

    /// <summary>
    /// Determines whether a given entry should be enabled under this preset.
    /// An empty EnabledEntries set means "enable all".
    /// </summary>
    public bool ShouldEnable(ContextMenuEntry entry)
    {
        if (EnabledEntries.Count == 0)
            return true;

        var rawLower = entry.RawName.ToLowerInvariant();
        var nameLower = entry.Name.ToLowerInvariant();

        return EnabledEntries.Contains(rawLower) || EnabledEntries.Contains(nameLower);
    }
}
