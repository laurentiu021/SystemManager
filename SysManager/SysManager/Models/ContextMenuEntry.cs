// SysManager · ContextMenuEntry — model for right-click context menu items
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// What kind of registration puts an item on the right-click menu. The two are read from different
/// registry shapes and hidden by different mechanisms, so a row has to say which it is.
/// </summary>
public enum ContextMenuEntryKind
{
    /// <summary>
    /// A verb under a class's <c>shell</c> key: a name and a command line. Hidden with
    /// <c>LegacyDisable</c>.
    /// </summary>
    MenuEntry,

    /// <summary>
    /// A COM shell extension registered under <c>shellex\ContextMenuHandlers</c> as a CLSID — the kind
    /// 7-Zip, Dropbox and antivirus software install, and the kind that makes the menu slow to open.
    /// Hidden by adding the CLSID to the machine-wide Blocked list, which needs administrator rights.
    /// </summary>
    Handler
}

/// <summary>
/// Represents a single Windows Explorer context menu entry discovered
/// from the registry shell keys. Toggling <see cref="IsEnabled"/> adds
/// or removes the <c>LegacyDisable</c> value — a standard non-destructive
/// mechanism Windows respects to hide the entry without deletion.
/// </summary>
public sealed partial class ContextMenuEntry : ObservableObject
{
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>Which registration shape this row came from. See <see cref="ContextMenuEntryKind"/>.</summary>
    public ContextMenuEntryKind Kind { get; init; } = ContextMenuEntryKind.MenuEntry;

    /// <summary>
    /// The COM class id for a <see cref="ContextMenuEntryKind.Handler"/>, in registry form including
    /// braces. Empty for a menu entry.
    /// </summary>
    public string Clsid { get; init; } = "";

    /// <summary>
    /// What to call this row in the UI: "Menu entry" or "Handler".
    /// </summary>
    public string KindLabel => Kind == ContextMenuEntryKind.Handler ? "Handler" : "Menu entry";

    /// <summary>
    /// Whether the row's switch can actually change anything.
    /// </summary>
    /// <remarks>
    /// False for handlers, because hiding one writes the machine-wide Blocked list and that path is not
    /// wired yet. The switch is disabled rather than absent so the row still reads as part of the same
    /// list — but it must be disabled, because a switch that moves and changes nothing is the worst of
    /// the three options.
    /// </remarks>
    public bool CanToggle => Kind == ContextMenuEntryKind.MenuEntry;

    /// <summary>Display name (from the shell key's Default value or key name).</summary>
    public required string Name { get; init; }

    /// <summary>The command line that executes when the entry is clicked.</summary>
    public required string Command { get; init; }

    /// <summary>Full registry path to the shell subkey (e.g. HKCR\*\shell\Open with Notepad).</summary>
    public required string RegistryPath { get; init; }

    /// <summary>Category: "Files", "Folders", "Desktop", or "Directory Background".</summary>
    public required string Location { get; init; }

    /// <summary>Application that added the entry (inferred from command path).</summary>
    public string Source { get; init; } = "";

    /// <summary>Original registry key name before friendly-name resolution.</summary>
    public string RawName { get; init; } = "";

    /// <summary>Whether this entry is considered a system/internal entry (raw name starts with @ or .).</summary>
    public bool IsSystemEntry { get; init; }

    /// <summary>Human-readable explanation of what this entry does when clicked.</summary>
    public string Explanation { get; init; } = "";
}
