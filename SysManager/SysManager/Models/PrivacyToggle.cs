// SysManager · PrivacyToggle — model for privacy/telemetry registry toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a single privacy toggle backed by a Windows registry value.
/// When <see cref="IsEnabled"/> is true, privacy protection is active
/// (i.e., the associated tracking/feature is DISABLED).
/// </summary>
public sealed partial class PrivacyToggle : ObservableObject
{
    [ObservableProperty] private bool _isEnabled;

    /// <summary>Short human-readable name shown in the toggle list.</summary>
    public required string Name { get; init; }

    /// <summary>Explanatory description displayed under the name.</summary>
    public required string Description { get; init; }

    /// <summary>Grouping category (e.g. Telemetry, UI Declutter, Features).</summary>
    public required string Category { get; init; }

    /// <summary>Full registry path (e.g. HKLM\SOFTWARE\...).</summary>
    public required string RegistryPath { get; init; }

    /// <summary>Registry value name to read/write.</summary>
    public required string ValueName { get; init; }

    /// <summary>Value written when privacy protection is ON.</summary>
    public required int EnabledValue { get; init; }

    /// <summary>Value written when privacy protection is OFF (default Windows state).</summary>
    public required int DisabledValue { get; init; }
}
