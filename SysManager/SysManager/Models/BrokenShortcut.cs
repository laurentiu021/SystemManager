// SysManager · BrokenShortcut — model for a broken .lnk file
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a .lnk shortcut whose target no longer exists.
/// </summary>
public partial class BrokenShortcut : ObservableObject
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
