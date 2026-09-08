// SysManager · IContextMenuService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and toggles Windows Explorer shell-extension entries. Extracted as an interface so the view
/// model's toggle path can be driven at all: without it a test could neither make a toggle FAIL on
/// purpose nor let it succeed, because a real toggle writes shell keys on the machine running the suite
/// — and on an elevated CI runner that write goes through (#2180, Gate-ARCH).
/// </summary>
/// <remarks>
/// What that blocked was not merely coverage. When a toggle fails, the view model explains it two
/// different ways depending on elevation, and the elevated one is the valuable half: it says the entry is
/// owned by TrustedInstaller and no amount of elevating will help, which is what stops a user relaunching
/// as administrator for nothing. Swapped, every user would take the useless path and nothing would fail.
/// <para>Only the three instance members the view model reaches are here. The classic-menu and
/// restart-Explorer operations stay static on <see cref="ContextMenuService"/>: the view model calls them
/// through the type rather than through an injected instance, so putting them behind this seam would
/// widen it without making anything newly testable.</para>
/// </remarks>
public interface IContextMenuService
{
    /// <summary>Scans the known registry shell locations for context-menu entries.</summary>
    List<ContextMenuEntry> ScanEntries();

    /// <summary>
    /// Hides an entry by writing <c>LegacyDisable</c>, without deleting any registration.
    /// Returns false when the key is missing or not writable — a TrustedInstaller-owned entry
    /// fails here even when elevated.
    /// </summary>
    bool DisableEntry(ContextMenuEntry entry);

    /// <summary>
    /// Restores an entry by removing <c>LegacyDisable</c>. Returns false on the same conditions
    /// as <see cref="DisableEntry"/>.
    /// </summary>
    bool EnableEntry(ContextMenuEntry entry);
}
