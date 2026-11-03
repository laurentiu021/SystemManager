// SysManager · RegistryPaths — the registry access PrivacyService and SettingsWatchdogService share
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;

namespace SysManager.Helpers;

/// <summary>
/// The seven registry paths <see cref="Services.PrivacyService"/> writes and
/// <see cref="Services.SettingsWatchdogService"/> re-reads to notice when Windows silently reverts
/// them, declared once so editing one can no longer miss the other (#2860). Watching your own writes
/// needs the same path in two services; it does not need the same path written out twice.
/// </summary>
/// <remarks>
/// Only the seven PATHS are shared here. Each service keeps its own value names, its own enabled/baseline
/// semantics, and — for <see cref="Services.SettingsWatchdogService"/> specifically — its own three Key
/// spellings that differ from Privacy's (<c>telemetry</c>, <c>activity-feed</c>, <c>lockscreen-ads</c> vs
/// Privacy's <c>diagnostic-data</c>, <c>activity-history</c>, <c>lock-screen-tips</c>): those Keys are
/// persisted in <c>settings-baseline.json</c> and <c>settings-drift.json</c> and joined by exact spelling
/// elsewhere, so unifying them would silently stop watching a setting for an existing baseline.
/// </remarks>
internal static class RegistryPaths
{
    internal const string DataCollectionPolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    internal const string WindowsSystemPolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System";
    internal const string AdvertisingInfo = @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    internal const string ContentDeliveryManager = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    internal const string ExplorerPolicy = @"HKCU\Software\Policies\Microsoft\Windows\Explorer";
    internal const string Dsh = @"HKLM\SOFTWARE\Policies\Microsoft\Dsh";

    /// <summary>
    /// Windows 10's news and interests policy (<c>EnableFeeds</c>), which <see cref="Dsh"/>'s widgets policy does not
    /// govern: on Windows 10 the widgets switch wrote that one and the weather button stayed (#2778).
    /// </summary>
    internal const string WindowsFeedsPolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds";

    /// <summary>
    /// Opens or creates the subkey a full path (<c>HKCU\...</c> or <c>HKLM\...</c>) names, under the
    /// caller's own already-resolved roots — never <see cref="Registry.CurrentUser"/> or
    /// <see cref="Registry.LocalMachine"/> directly, so a caller redirecting its roots for a different
    /// signed-in account or a test is honoured. Returns null for anything that is not one of the two
    /// recognised hives.
    /// </summary>
    internal static RegistryKey? OpenOrCreateKey(RegistryKey hkcuRoot, RegistryKey hklmRoot, string fullPath, bool writable)
    {
        if (Split(fullPath) is not { } split) return null;

        var hive = split.Hive == RegistryHive.CurrentUser ? hkcuRoot : hklmRoot;
        return writable
            ? hive.CreateSubKey(split.SubPath, writable: true)
            : hive.OpenSubKey(split.SubPath, writable: false);
    }

    /// <summary>
    /// The hive a full path names, <see cref="RegistryHive.CurrentUser"/> (<c>HKCU</c>) or
    /// <see cref="RegistryHive.LocalMachine"/> (<c>HKLM</c>), and the path below it; null for any other hive or a path
    /// that names no key below one.
    /// </summary>
    internal static (RegistryHive Hive, string SubPath)? Split(string fullPath)
    {
        var separatorIndex = fullPath.IndexOf('\\');
        if (separatorIndex < 0) return null;

        RegistryHive? hive = fullPath[..separatorIndex].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => null
        };
        return hive is { } named ? (named, fullPath[(separatorIndex + 1)..]) : null;
    }

    /// <summary>
    /// True for a key under <c>HKCU\Software\Policies</c>, where Windows keeps the policies set for one account. The
    /// account may read them but only SYSTEM and administrators may write them, so that a user cannot lift a policy
    /// set for them, which makes writing one an administrator's change though it changes one account (#2778).
    /// </summary>
    internal static bool IsPerUserPolicy(string fullPath) =>
        Split(fullPath) is { Hive: RegistryHive.CurrentUser, SubPath: var subPath }
        && subPath.StartsWith(PoliciesBelowTheHive, StringComparison.OrdinalIgnoreCase)
        && (subPath.Length == PoliciesBelowTheHive.Length || subPath[PoliciesBelowTheHive.Length] == '\\');

    private const string PoliciesBelowTheHive = @"Software\Policies";
}
