// SysManager · HostsEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a single line in the Windows hosts file.
/// </summary>
public sealed partial class HostsEntry : ObservableObject
{
    /// <summary>What a screen reader calls the grid row, rather than the class's type name (#2785).</summary>
    public override string ToString() => $"{Hostname}, {IpAddress}";

    [ObservableProperty] private bool _isEnabled = true;
    public required string IpAddress { get; init; }
    public required string Hostname { get; init; }
    public string Comment { get; init; } = "";
}
