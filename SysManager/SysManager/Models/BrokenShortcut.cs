// SysManager · BrokenShortcut — model for a broken .lnk file
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a .lnk shortcut whose target no longer exists.
/// </summary>
public sealed partial class BrokenShortcut : ObservableObject, Helpers.ISelectableRow
{
    /// <summary>Display name of the shortcut (without .lnk extension).</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>Full path to the .lnk file.</summary>
    [ObservableProperty] private string _shortcutPath = "";

    /// <summary>The target path the shortcut points to (which no longer exists).</summary>
    [ObservableProperty] private string _targetPath = "";

    /// <summary>Location category (Desktop, Start Menu, etc.).</summary>
    [ObservableProperty] private string _location = "";

    /// <summary>Whether this shortcut is selected for deletion.</summary>
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// What a shortcut scan found: the shortcuts whose target is CONFIRMED gone, and how many it could not
/// decide about.
/// </summary>
/// <remarks>
/// The count is the point of the record existing. A target the scan could not reach — a drive that is not
/// attached, a share that is asleep, a path this user cannot stat — is not a broken shortcut, so it is left
/// out of <see cref="Broken"/>; but every row in that list arrives pre-ticked for deletion, so silently
/// dropping the undecided ones would leave the user with no way to tell "your PC is clean" from "I could not
/// check" (#2378).
/// </remarks>
public sealed record ShortcutScanReport
{
    /// <summary>Shortcuts whose target was established to be gone. Safe to offer for deletion.</summary>
    public IReadOnlyList<BrokenShortcut> Broken { get; init; } = [];

    /// <summary>
    /// Shortcuts left alone because the scan could not reach their target to judge it.
    /// </summary>
    public int UnreachableTargets { get; init; }

    /// <summary>
    /// A plain-English sentence for the user, or null when there is nothing they need to know. Names the
    /// consequence (these were left alone, and why that is the safe answer) rather than the mechanism.
    /// </summary>
    public string? Notice => UnreachableTargets == 0
        ? null
        : $"{UnreachableTargets} shortcut{(UnreachableTargets == 1 ? "" : "s")} "
          + $"{(UnreachableTargets == 1 ? "points" : "point")} somewhere this scan could not look — a drive "
          + "that is not plugged in, a network folder that is not answering, or a place you do not have "
          + $"permission to read. {(UnreachableTargets == 1 ? "It was" : "They were")} left alone, because "
          + "not being able to check is not the same as being broken.";
}
