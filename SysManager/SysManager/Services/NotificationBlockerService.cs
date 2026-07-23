// SysManager · NotificationBlockerService — per-app and global toast notification control
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Mutes app notification nags using the same documented HKCU registry values the Windows
/// Settings &gt; System &gt; Notifications page writes — nothing deeper. Two levers:
/// the per-app <c>Enabled</c> DWORD under <c>Notifications\Settings\&lt;AUMID&gt;</c>
/// (the per-app switch) and the user-wide <c>PushNotifications\ToastEnabled</c> master
/// toggle (shared with <see cref="NotificationsTweak"/> in the Gaming Profile).
///
/// Deliberately NOT here: window-hooking/pop-up interception (issue #340's original idea) —
/// that would mean injecting into or manipulating other processes' windows, which is
/// invasive, fragile, and indistinguishable from malware heuristics. Muting an app's toast
/// channel is the safe, supported way Windows itself offers; everything is per-user
/// (no admin) and reversible by flipping the same switch back.
///
/// The registry root is injectable (defaulting to <see cref="Registry.CurrentUser"/>) so the
/// logic is unit-testable against a redirected subkey (mirrors <see cref="AppBlockerService"/>,
/// <see cref="WindowsUpdatePolicyService"/>).
/// </summary>
public sealed class NotificationBlockerService : INotificationBlockerService
{
    // Per-app senders live one subkey per AUMID under this path; the per-app switch in
    // Windows Settings writes an "Enabled" DWORD (0 = muted, absent/1 = allowed) there.
    internal const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    internal const string EnabledValueName = "Enabled";

    // The user-wide master toggle (same values NotificationsTweak uses for Gaming Profile).
    internal const string PushKeyPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    internal const string ToastValueName = "ToastEnabled";

    // Display names for AUMIDs are registered here by desktop apps that show toasts.
    private const string AumidClassesPath = @"Software\Classes\AppUserModelId";

    // Windows' own housekeeping subkeys under Notifications\Settings that are not app senders.
    private static readonly HashSet<string> NonAppSubkeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Explorer.Notification", // legacy shell bucket, not a user-facing sender
    };

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// Creates the service over a registry root. Defaults to <see cref="Registry.CurrentUser"/>
    /// (the real per-user notification settings); tests pass a redirected root (a disposable
    /// HKCU subkey) so reads/writes never touch the machine's real configuration.
    /// </summary>
    public NotificationBlockerService(RegistryKey? baseKey = null)
        => _baseKey = baseKey ?? Registry.CurrentUser;

    /// <inheritdoc />
    public bool IsGlobalToastEnabled()
    {
        try
        {
            using var key = _baseKey.OpenSubKey(PushKeyPath);
            // Absent value = notifications on (Windows default).
            return key?.GetValue(ToastValueName) is not int v || v != 0;
        }
        catch (System.Security.SecurityException ex) { Log.Debug("Toast master read denied: {Error}", ex.Message); return true; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Toast master read denied: {Error}", ex.Message); return true; }
    }

    /// <inheritdoc />
    public bool SetGlobalToastEnabled(bool enabled)
    {
        try
        {
            using var key = _baseKey.CreateSubKey(PushKeyPath, writable: true);
            if (enabled)
                key.DeleteValue(ToastValueName, throwOnMissingValue: false); // absent = default = on
            else
                key.SetValue(ToastValueName, 0, RegistryValueKind.DWord);
            Log.Information("Notifications: master toggle set to {Enabled}", enabled);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("Toast master write denied: {Error}", ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("Toast master write denied: {Error}", ex.Message); return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<NotificationApp> GetApps()
    {
        List<NotificationApp> apps = [];
        try
        {
            using var settings = _baseKey.OpenSubKey(SettingsPath);
            if (settings is null) return apps;

            foreach (var aumid in settings.GetSubKeyNames())
            {
                if (NonAppSubkeys.Contains(aumid)) continue;

                try
                {
                    using var appKey = settings.OpenSubKey(aumid);
                    if (appKey is null) continue;

                    // Absent Enabled value = notifications allowed (Windows default).
                    var enabled = appKey.GetValue(EnabledValueName) is not int e || e != 0;
                    var count = appKey.GetValue("PeriodicNotificationCount") is int c ? c : 0;

                    DateTime? last = null;
                    // FILETIME (100ns ticks since 1601) stored as QWORD; registry returns it as long.
                    if (appKey.GetValue("LastNotificationAddedTime") is long ft && ft > 0)
                    {
                        try { last = DateTime.FromFileTime(ft); }
                        catch (ArgumentOutOfRangeException) { /* corrupt timestamp — leave null */ }
                    }

                    apps.Add(new NotificationApp
                    {
                        Aumid = aumid,
                        DisplayName = ResolveDisplayName(aumid),
                        RecentCount = count,
                        LastNotification = last,
                        IsEnabled = enabled,
                    });
                }
                catch (System.Security.SecurityException) { /* skip unreadable sender */ }
                catch (UnauthorizedAccessException) { /* skip unreadable sender */ }
                catch (System.IO.IOException) { /* skip unreadable sender */ }
            }
        }
        catch (System.Security.SecurityException ex) { Log.Debug("Notification senders read denied: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Notification senders read denied: {Error}", ex.Message); }
        catch (System.IO.IOException ex) { Log.Debug("Notification senders read failed: {Error}", ex.Message); }

        return apps
            .OrderByDescending(a => a.LastNotification ?? DateTime.MinValue)
            .ThenByDescending(a => a.RecentCount)
            .ToList();
    }

    /// <inheritdoc />
    public bool SetAppEnabled(string aumid, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        // AUMIDs are registry subkey names we enumerated ourselves, but the write API is
        // public on the seam — refuse separators so a caller can never escape SettingsPath.
        if (aumid.Contains('\\') || aumid.Contains('/')) return false;

        try
        {
            using var settings = _baseKey.OpenSubKey(SettingsPath, writable: true);
            using var appKey = settings?.OpenSubKey(aumid, writable: true);
            if (appKey is null) return false;

            if (enabled)
                appKey.DeleteValue(EnabledValueName, throwOnMissingValue: false); // absent = default = on
            else
                appKey.SetValue(EnabledValueName, 0, RegistryValueKind.DWord);

            Log.Information("Notifications: {Aumid} set to {Enabled}", aumid, enabled);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("Notification toggle denied for {Aumid}: {Error}", aumid, ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("Notification toggle denied for {Aumid}: {Error}", aumid, ex.Message); return false; }
        catch (System.IO.IOException ex) { Log.Warning("Notification toggle failed for {Aumid}: {Error}", aumid, ex.Message); return false; }
    }

    /// <summary>
    /// Best human-readable name for an AUMID: the DisplayName a desktop app registered under
    /// <c>HKCU\Software\Classes\AppUserModelId</c>, else a prettified tail of the AUMID itself
    /// (e.g. <c>com.squirrel.slack.slack</c> → <c>Slack</c>).
    /// </summary>
    internal string ResolveDisplayName(string aumid)
    {
        try
        {
            using var reg = _baseKey.OpenSubKey($@"{AumidClassesPath}\{aumid}");
            if (reg?.GetValue("DisplayName") is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (System.Security.SecurityException) { /* fall through to prettify */ }
        catch (UnauthorizedAccessException) { /* fall through to prettify */ }

        return PrettifyAumid(aumid);
    }

    /// <summary>
    /// Derives a readable label from an AUMID when no DisplayName registration exists.
    /// Handles the common shapes: reverse-DNS (<c>com.squirrel.slack.slack</c>),
    /// packaged-app family names (<c>Microsoft.WindowsStore_8wekyb…!App</c>), and
    /// system toasts (<c>Windows.SystemToast.StartupApp</c>).
    /// </summary>
    internal static string PrettifyAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return aumid;

        var s = aumid;

        // Packaged apps: strip the "!App" entry-point suffix and the publisher-hash suffix.
        var bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        var underscore = s.IndexOf('_');
        if (underscore > 0) s = s[..underscore];

        // System toasts read better without the fixed prefix.
        const string systemToast = "Windows.SystemToast.";
        if (s.StartsWith(systemToast, StringComparison.OrdinalIgnoreCase))
            s = s[systemToast.Length..];

        // Reverse-DNS and dotted family names: the last segment is the app name
        // (com.squirrel.slack.slack → slack; Microsoft.Office.OUTLOOK.EXE.15 → keep readable tail).
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            // Numeric or "EXE"-style tails aren't names — walk back to the last wordy segment.
            var last = Array.FindLast(parts, p =>
                p.Length > 2 && !int.TryParse(p, out _) &&
                !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? parts[^1];
            s = last;
        }

        // Capitalize the first letter so "slack" reads as "Slack".
        return s.Length > 0 && char.IsLower(s[0])
            ? char.ToUpperInvariant(s[0]) + s[1..]
            : s;
    }
}
