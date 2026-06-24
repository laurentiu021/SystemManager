// SysManager · PathEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// One directory inside a PATH-like variable's ';'-separated list. Observable flags
/// drive the editor's highlighting: <see cref="IsMissing"/> (directory does not exist)
/// and <see cref="IsDuplicate"/> (same directory appears earlier in the list).
/// </summary>
public sealed partial class PathEntry : ObservableObject
{
    [ObservableProperty] private string _directory = "";
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isDuplicate;

    public PathEntry(string directory) => _directory = directory;
}
