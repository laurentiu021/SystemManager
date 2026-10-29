// SysManager · RegistryPaths — the registry access PrivacyService and SettingsWatchdogService share
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;

namespace SysManager.Helpers;

/// <summary>
/// The six registry paths <see cref="Services.PrivacyService"/> writes and
/// <see cref="Services.SettingsWatchdogService"/> re-reads to notice when Windows silently reverts
/// them, declared once so editing one can no longer miss the other (#2860). Watching your own writes
/// needs the same path in two services; it does not need the same path written out twice.
/// </summary>
/// <remarks>
/// Only the six PATHS are shared here. Each service keeps its own value names, its own enabled/baseline
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
    /// Opens or creates the subkey a full path (<c>HKCU\...</c> or <c>HKLM\...</c>) names, under the
    /// caller's own already-resolved roots — never <see cref="Registry.CurrentUser"/> or
    /// <see cref="Registry.LocalMachine"/> directly, so a caller redirecting its roots for a different
    /// signed-in account or a test is honoured. Returns null for anything that is not one of the two
    /// recognised hives.
    /// </summary>
    internal static RegistryKey? OpenOrCreateKey(RegistryKey hkcuRoot, RegistryKey hklmRoot, string fullPath, bool writable)
    {
        var separatorIndex = fullPath.IndexOf('\\');
        if (separatorIndex < 0) return null;

        var hiveName = fullPath[..separatorIndex].ToUpperInvariant();
        var subPath = fullPath[(separatorIndex + 1)..];

        var hive = hiveName switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => hkcuRoot,
            "HKLM" or "HKEY_LOCAL_MACHINE" => hklmRoot,
            _ => null
        };

        if (hive is null) return null;

        return writable ? hive.CreateSubKey(subPath, writable: true) : hive.OpenSubKey(subPath, writable: false);
    }
}
