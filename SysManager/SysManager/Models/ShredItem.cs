// SysManager · ShredItem — model for a file/folder queued for secure deletion
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Represents a file or folder queued for secure shredding.
/// </summary>
public sealed partial class ShredItem : ObservableObject
{
    [ObservableProperty] private string _status = "Pending";

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public bool IsFolder { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);
}
