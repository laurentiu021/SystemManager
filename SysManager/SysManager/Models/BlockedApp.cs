// SysManager · BlockedApp — model for a blocked application
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents an application that has been blocked from executing via IFEO.
/// </summary>
public partial class BlockedApp : ObservableObject
{
    /// <summary>Executable file name (e.g., "notepad.exe").</summary>
    [ObservableProperty] private string _executableName = "";

    /// <summary>Full path to the executable (if known).</summary>
    [ObservableProperty] private string _fullPath = "";

    /// <summary>When the block was applied.</summary>
    [ObservableProperty] private DateTime _blockedAt;

    /// <summary>Whether this entry is selected in the UI.</summary>
    [ObservableProperty] private bool _isSelected;
}
