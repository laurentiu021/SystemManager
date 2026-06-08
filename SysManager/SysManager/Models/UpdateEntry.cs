// SysManager · UpdateEntry — model for Windows Update entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a Windows Update entry (available, hidden, or from history).
/// </summary>
public sealed partial class UpdateEntry : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "";

    public string Title { get; init; } = "";
    public string KB { get; init; } = "";
    public string Size { get; init; } = "";
    public DateTime? Date { get; init; }
    public bool IsHidden { get; init; }
    public string Category { get; init; } = "";
    public string UpdateId { get; init; } = "";

    /// <summary>Formatted date for display (yyyy-MM-dd or empty).</summary>
    public string DateDisplay => Date?.ToString("yyyy-MM-dd") ?? "";
}
