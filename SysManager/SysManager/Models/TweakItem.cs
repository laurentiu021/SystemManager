// SysManager · TweakItem — a selectable, reversible optimization in the Tweaks Hub
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>Risk tier for a tweak in the hub.</summary>
public enum TweakTier
{
    /// <summary>Low-risk, broadly recommended, per-user (HKCU) — applies without admin.</summary>
    Essential,
    /// <summary>Higher-impact, machine-wide (HKLM) — needs administrator.</summary>
    Advanced,
}

/// <summary>
/// One reversible optimization shown in the Tweaks Hub, wrapping the underlying
/// <see cref="PrivacyToggle"/> so the hub is a unified front-end over the same
/// already-reversible registry operations (no parallel implementation). The tier is
/// derived from the toggle's registry hive — the real risk/elevation boundary.
/// <see cref="IsSelected"/> is the user's tick; <see cref="IsApplied"/> reflects the
/// current system state read from the registry.
/// </summary>
public sealed partial class TweakItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isApplied;

    public required PrivacyToggle Toggle { get; init; }
    public required TweakTier Tier { get; init; }

    public string Name => Toggle.Name;
    public string Description => Toggle.Description;
    public string Category => Toggle.Category;

    /// <summary>Builds a hub item from a privacy toggle, classifying its tier by registry hive.</summary>
    public static TweakItem From(PrivacyToggle toggle) => new()
    {
        Toggle = toggle,
        Tier = ClassifyTier(toggle.RegistryPath),
        IsApplied = toggle.IsEnabled,
    };

    /// <summary>
    /// Pure tier classification: HKLM (machine-wide, needs admin) is Advanced; everything
    /// else (HKCU, per-user) is Essential. Unit-testable without the registry.
    /// </summary>
    public static TweakTier ClassifyTier(string registryPath) =>
        registryPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ||
        registryPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
            ? TweakTier.Advanced
            : TweakTier.Essential;
}
