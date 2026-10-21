// SysManager · UpdateEntry — model for Windows Update entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a Windows Update entry (available, hidden, or from history).
/// </summary>
public sealed partial class UpdateEntry : ObservableObject, Helpers.ISelectableRow
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "";

    public string Title { get; init; } = "";
    public string KB { get; init; } = "";
    /// <summary>The download size as text, such as "15.2 MB", or empty when Windows gave none.</summary>
    public string SizeDisplay { get; init; } = "";

    /// <summary>
    /// The download size in bytes, which the Size column sorts on: sorting <see cref="SizeDisplay"/> put "1.2 GB"
    /// before "15.0 MB" (#2819). 0 when Windows gave none, as for the history.
    /// </summary>
    public long SizeBytes { get; init; }
    public DateTime? Date { get; init; }
    public bool IsHidden { get; init; }
    public string Category { get; init; } = "";
    public string UpdateId { get; init; } = "";

    /// <summary>Formatted date for display (yyyy-MM-dd or empty).</summary>
    public string DateDisplay => Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}
