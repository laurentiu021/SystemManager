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
    [ObservableProperty] private bool _isEnabled = true;
    public required string IpAddress { get; init; }
    public required string Hostname { get; init; }
    public string Comment { get; init; } = "";
}
