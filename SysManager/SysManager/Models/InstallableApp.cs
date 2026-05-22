// SysManager · InstallableApp — model for bulk-installable applications
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A curated application available for bulk installation via winget.
/// </summary>
public sealed partial class InstallableApp : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    public required string Name { get; init; }
    public required string WingetId { get; init; }
    public required string Category { get; init; }
    public string Description { get; init; } = "";
    public string IconGlyph { get; init; } = "";
}
