// SysManager · ContextMenuEntry — model for right-click context menu items
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a single Windows Explorer context menu entry discovered
/// from the registry shell keys. Toggling <see cref="IsEnabled"/> adds
/// or removes the <c>LegacyDisable</c> value — a standard non-destructive
/// mechanism Windows respects to hide the entry without deletion.
/// </summary>
public sealed partial class ContextMenuEntry : ObservableObject
{
    [ObservableProperty] private bool _isEnabled = true;

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
}
