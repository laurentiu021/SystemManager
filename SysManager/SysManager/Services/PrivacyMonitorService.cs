// SysManager · PrivacyMonitorService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads which applications recently accessed sensitive capabilities (camera, microphone,
/// location) from the Windows CapabilityAccessManager ConsentStore. Strictly read-only —
/// it reports access history; it never grants or revokes permissions itself (the UI links
/// to the Windows privacy settings for that).
///
/// The registry root is injectable so the decoding/parsing can be unit-tested against a
/// redirected HKCU subkey without depending on the machine's real consent history.
/// </summary>
public sealed class PrivacyMonitorService
{
    private const string ConsentBase =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    // Registry capability folder → friendly label.
    private static readonly (string Key, string Label)[] Capabilities =
    [
        ("webcam", "Camera"),
        ("microphone", "Microphone"),
        ("location", "Location"),
    ];

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// Creates the service over a registry root. Defaults to <see cref="Registry.CurrentUser"/>
    /// (the real per-user consent store); tests pass a redirected root.
    /// </summary>
    public PrivacyMonitorService(RegistryKey? baseKey = null)
        => _baseKey = baseKey ?? Registry.CurrentUser;

    /// <summary>Reads the access history for all capabilities, most-recent first.</summary>
    public IReadOnlyList<PrivacyAccessEntry> Read()
    {
        List<PrivacyAccessEntry> entries = [];
        foreach (var (capKey, label) in Capabilities)
        {
            try
            {
                using var cap = _baseKey.OpenSubKey($@"{ConsentBase}\{capKey}");
                if (cap is null) continue;
                CollectFrom(cap, label, entries);

                // Desktop (non-Store) apps live one level deeper under NonPackaged.
                using var nonPackaged = cap.OpenSubKey("NonPackaged");
                if (nonPackaged is not null) CollectFrom(nonPackaged, label, entries);
            }
            catch (System.Security.SecurityException ex) { Log.Debug("Privacy monitor read denied for {Cap}: {Error}", capKey, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Privacy monitor read denied for {Cap}: {Error}", capKey, ex.Message); }
        }
        return [.. entries.OrderByDescending(e => e.InUse).ThenByDescending(e => e.LastUsed ?? DateTime.MinValue)];
    }

    private static void CollectFrom(RegistryKey capKey, string label, List<PrivacyAccessEntry> entries)
    {
        foreach (var appKeyName in capKey.GetSubKeyNames())
        {
            if (appKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var appKey = capKey.OpenSubKey(appKeyName);
                if (appKey is null) continue;

                var start = ToFileTime(appKey.GetValue("LastUsedTimeStart"));
                var stop = ToFileTime(appKey.GetValue("LastUsedTimeStop"));
                // In use now: a fresh Start with no Stop, or a Start newer than the last Stop
                // (a force-killed app may leave a stale Stop that predates its current Start).
                var inUse = start.HasValue && (!stop.HasValue || start > stop);

                DateTime? lastUsed = null;
                // Report the most recent of the two timestamps, not always Stop.
                var ticks = start.HasValue && stop.HasValue ? Math.Max(start.Value, stop.Value) : stop ?? start;
                if (ticks.HasValue) lastUsed = FileTimeToLocal(ticks.Value);

                entries.Add(new PrivacyAccessEntry(label, FriendlyAppName(appKeyName), lastUsed, inUse));
            }
            // A single malformed/unreadable app subkey must not abort the whole capability scan.
            catch (System.Security.SecurityException ex) { Log.Debug("Privacy monitor skipped app key {App}: {Error}", appKeyName, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Privacy monitor skipped app key {App}: {Error}", appKeyName, ex.Message); }
        }
    }

    /// <summary>Coerces a registry value (QWORD FILETIME) to a long, or null.</summary>
    internal static long? ToFileTime(object? value) => value switch
    {
        long l when l > 0 => l,
        int i when i > 0 => i,
        _ => null
    };

    /// <summary>Converts a Windows FILETIME (100ns ticks since 1601) to local time.</summary>
    internal static DateTime FileTimeToLocal(long fileTime)
    {
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Turns a ConsentStore app-key name into a friendly name. Non-packaged desktop apps are
    /// stored as their full path with '#' replacing '\'; we take the executable's file name.
    /// Packaged apps use their package family name, of which we take the leading portion.
    /// </summary>
    internal static string FriendlyAppName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return "Unknown app";

        if (keyName.Contains('#'))
        {
            // e.g. "C:#Program Files#App#app.exe" → "app.exe". A degenerate key that is only
            // separators ("#", "##") splits to an empty array — fall back to the raw key.
            var parts = keyName.Split('#', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : keyName;
        }

        // Packaged: "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Microsoft.WindowsCamera"
        var underscore = keyName.IndexOf('_');
        return underscore > 0 ? keyName[..underscore] : keyName;
    }
}
