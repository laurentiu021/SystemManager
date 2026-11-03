// SysManager · PrivacyToggle — model for privacy/telemetry registry toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Represents a single privacy toggle backed by a Windows registry value.
/// When <see cref="IsEnabled"/> is true, privacy protection is active
/// (i.e., the associated tracking/feature is DISABLED).
/// </summary>
public sealed partial class PrivacyToggle : ObservableObject
{
    [ObservableProperty] private bool _isEnabled;

    /// <summary>
    /// True while the switch differs from what is applied, so its row can say it changes when Apply is pressed
    /// (#1517). Set by the view model that owns the baseline.
    /// </summary>
    [ObservableProperty] private bool _isPending;

    /// <summary>
    /// The stable name a privacy profile gives this toggle (e.g. <c>diagnostic-data</c>). Unlike
    /// <see cref="Name"/> it is never reworded: a profile exported by one version names its toggles by key, so
    /// renaming a key would make every profile already sent to someone mean less than it says.
    /// </summary>
    public required string Key { get; init; }

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

    /// <summary>
    /// The other values this switch writes with <see cref="ValueName"/>, in the same key, to the same values, and
    /// removes with it: those Windows' own switch for the same thing sets together. The switch reads as on only when
    /// every one of them is <see cref="EnabledValue"/>. Empty for a switch with one value (#2778).
    /// </summary>
    public IReadOnlyList<string> AlsoWrites { get; init; } = [];

    /// <summary>
    /// <see cref="ValueName"/>, then <see cref="AlsoWrites"/>: every value the switch reads and writes.
    /// </summary>
    public IReadOnlyList<string> ValueNames => [ValueName, .. AlsoWrites];

    /// <summary>Value written when privacy protection is ON.</summary>
    public required int EnabledValue { get; init; }

    /// <summary>Value written when privacy protection is OFF (default Windows state).</summary>
    public required int DisabledValue { get; init; }

    /// <summary>
    /// The other values the setting is known to take, besides <see cref="EnabledValue"/> and
    /// <see cref="DisabledValue"/>: the levels between them, for a policy that has some.
    /// </summary>
    public IReadOnlyList<int> KnownValues { get; init; } = [];

    /// <summary>True when <paramref name="value"/> is one the setting is known to take.</summary>
    /// <remarks>
    /// Turning a switch off puts back what its value held, read from a file in the user's own profile, which anything
    /// running as the user can change. A switch that needs administrator writes a key the user cannot, so it puts back
    /// only a known value, as Settings Watchdog's Restore does (#2747, #2778).
    /// </remarks>
    public bool TakesValue(int value) => value == EnabledValue || value == DisabledValue || KnownValues.Contains(value);

    /// <summary>Whose settings this switch changes, read from <see cref="RegistryPath"/>.</summary>
    public PrivacyReach Reach => ReachOf(RegistryPath);

    /// <summary>
    /// True when only an administrator can write this switch: it changes Windows for everyone on the PC, or it is a
    /// policy Windows keeps for this account.
    /// </summary>
    public bool NeedsAdministrator => Reach != PrivacyReach.JustYou;

    /// <summary>
    /// Pure: a machine-wide key (HKLM) changes Windows for everyone on the PC and needs administrator. A per-user
    /// policy (<c>HKCU\Software\Policies</c>) changes only the signed-in account but needs administrator too, because
    /// Windows lets the account read its policies and not write them; it was listed as needing nothing (#2778).
    /// Anything else under HKCU changes only the signed-in account. Unit-testable without the registry.
    /// </summary>
    public static PrivacyReach ReachOf(string registryPath)
    {
        if (RegistryPaths.Split(registryPath) is { Hive: RegistryHive.LocalMachine })
            return PrivacyReach.EveryoneOnThisPc;
        return RegistryPaths.IsPerUserPolicy(registryPath) ? PrivacyReach.JustYouByPolicy : PrivacyReach.JustYou;
    }
}
