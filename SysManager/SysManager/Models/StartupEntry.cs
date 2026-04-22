// SysManager · StartupEntry — model for startup items
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A single program that runs at Windows startup. Toggling IsEnabled
/// renames the registry value (adds/removes a "Disabled_" prefix) —
/// completely non-destructive and reversible.
/// </summary>
public partial class StartupEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _location = "";      // e.g. "HKCU\...\Run"
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private StartupSource _source;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Registry key path (for registry-based entries).</summary>
    public string RegistryKey { get; init; } = "";

    /// <summary>Registry value name (original, without Disabled_ prefix).</summary>
    public string ValueName { get; init; } = "";

    /// <summary>Task Scheduler path (for scheduled task entries).</summary>
    public string TaskPath { get; init; } = "";
}

public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    TaskScheduler
}
