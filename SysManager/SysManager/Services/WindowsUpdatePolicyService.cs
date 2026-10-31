// SysManager · WindowsUpdatePolicyService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and writes when Windows installs updates: the documented deferral policy for feature updates
/// (HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate), and a bounded pause through the values the Settings
/// app's own Pause updates writes (HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings). Every change is fully
/// reversible by clearing those values. There is intentionally NO "disable all updates" option: the strongest action
/// is a clearly-bounded pause, so a machine is never left permanently unpatched.
///
/// The registry root is injectable so the logic can be unit-tested against a redirected
/// HKCU subkey without administrator rights or touching real machine policy (mirrors
/// <see cref="AppBlockerService"/>).
/// </summary>
/// <remarks>
/// The pause used to be written into the policy key as start and end times for feature and quality updates. The
/// policy defines no end time: its two start values are a <c>yyyy-mm-dd</c> date, and "this pause will remain in
/// effect for 35 days from the specified start date or until the field is cleared". So the pause the tab showed, until
/// a date N days away, was not the one Windows applied (#2726). The Settings app's values carry the end the user chose,
/// Windows resumes updates at it on its own, and Settings shows the pause with its own Resume button.
/// </remarks>
public sealed class WindowsUpdatePolicyService
{
    // Relative path under the base key. Under HKLM this is the real Group Policy location
    // the Windows Update client honors.
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    /// <summary>Where the Settings app keeps its own pause, relative to the base key.</summary>
    internal const string SettingsPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    // Max bounded pause Windows itself honors (~35 days); we never exceed it.
    public const int MaxPauseDays = 35;

    /// <summary>How long a pause in the policy lasts from its start date, as the policy's own explanation says.</summary>
    internal const int PolicyPauseDays = 35;

    /// <summary>The form the Settings app writes its pause times in: UTC, to the second.</summary>
    internal const string SettingsTimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>The Settings app's pause starts, all set to the same time.</summary>
    internal static readonly string[] SettingsPauseStarts =
        ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];

    /// <summary>The Settings app's pause ends, all set to the same time.</summary>
    internal static readonly string[] SettingsPauseEnds =
        ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    /// <summary>
    /// The pause values earlier versions wrote into the policy key. Only the two start values are policy, a date the
    /// pause runs 35 days from; the two end values are no policy at all.
    /// </summary>
    internal static readonly string[] PolicyPauseValues =
    [
        "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    ];

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// Creates the service over a registry root. Defaults to
    /// <see cref="Registry.LocalMachine"/> (real machine policy, needs admin); tests pass a
    /// redirected root (an HKCU subkey) to avoid admin and machine writes.
    /// </summary>
    public WindowsUpdatePolicyService(RegistryKey? baseKey = null)
        => _baseKey = baseKey ?? Registry.LocalMachine;

    /// <summary>
    /// Reads the current deferral and pause, or null when Windows refused the read.
    /// </summary>
    /// <remarks>
    /// Null, not the defaults, for a refused read. The defaults are what a PC with no policy
    /// has, so a PC whose deferral or pause could not be read was shown "Default — Windows manages update
    /// timing.", and <b>Restore default</b> quoted that as the state it was about to clear (#2504). No policy key
    /// at all is still the defaults: that is a read that found nothing.
    /// <para>The pause is the later of the Settings app's end and a start date in the policy plus 35 days. A start
    /// date there is set by Group Policy, or left by an earlier version, whose start times are read by their date;
    /// either way 35 days from it is the longest Windows pauses for it.</para>
    /// </remarks>
    public WindowsUpdatePolicy? Read(DateTime now)
    {
        try
        {
            var defer = false;
            var days = 0;
            DateTime? policyPauseEnd = null;
            using (var key = _baseKey.OpenSubKey(PolicyPath))
            {
                if (key is not null)
                {
                    defer = key.GetValue("DeferFeatureUpdates") is int d && d == 1;
                    days = key.GetValue("DeferFeatureUpdatesPeriodInDays") is int n ? n : 0;
                    policyPauseEnd = Later(PolicyPauseStart(key, "PauseFeatureUpdatesStartTime"),
                                           PolicyPauseStart(key, "PauseQualityUpdatesStartTime"))?.AddDays(PolicyPauseDays);
                }
            }

            DateTime? settingsPauseEnd = null;
            using (var settings = _baseKey.OpenSubKey(SettingsPath))
            {
                if (settings is not null)
                    foreach (var name in SettingsPauseEnds)
                        settingsPauseEnd = Later(settingsPauseEnd, SettingsTime(settings, name));
            }

            var pauseUntil = Later(policyPauseEnd, settingsPauseEnd);
            var paused = pauseUntil is { } u && u > now;

            return new WindowsUpdatePolicy(defer, days, paused, paused ? pauseUntil : null);
        }
        catch (System.Security.SecurityException ex) { Log.Debug("WU policy read denied: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("WU policy read denied: {Error}", ex.Message); }
        return null;
    }

    /// <summary>Clamps a requested defer-days value into the range Windows accepts (0–365).</summary>
    public static int ClampDeferDays(int days) => Math.Clamp(days, 0, 365);

    /// <summary>Clamps a requested pause window into 1–<see cref="MaxPauseDays"/> days.</summary>
    public static int ClampPauseDays(int days) => Math.Clamp(days, 1, MaxPauseDays);

    /// <summary>
    /// Defers feature updates by the given number of days (quality/security updates keep
    /// flowing). Returns false if the write was denied (no elevation).
    /// </summary>
    public bool DeferFeatureUpdates(int days)
    {
        var clamped = ClampDeferDays(days);
        return Write(PolicyPath, key =>
        {
            key.SetValue("DeferFeatureUpdates", 1, RegistryValueKind.DWord);
            key.SetValue("DeferFeatureUpdatesPeriodInDays", clamped, RegistryValueKind.DWord);
        }, $"defer feature updates {clamped}d");
    }

    /// <summary>
    /// Pauses updates for a bounded window (clamped to <see cref="MaxPauseDays"/>), the way the Settings app's own
    /// Pause updates does, after which Windows resumes them on its own. Returns false if the write was denied.
    /// </summary>
    /// <remarks>
    /// The pause values an earlier version left in the policy key are cleared first, since a start date there would
    /// keep updates paused for up to 35 days whatever was chosen here.
    /// </remarks>
    public bool PauseUpdates(int days, DateTime now)
    {
        var start = now.ToUniversalTime();
        var end = start.AddDays(ClampPauseDays(days));
        return ClearPolicyPause()
            && Write(SettingsPath, key =>
            {
                foreach (var name in SettingsPauseStarts) key.SetValue(name, Format(start), RegistryValueKind.String);
                foreach (var name in SettingsPauseEnds) key.SetValue(name, Format(end), RegistryValueKind.String);
            }, $"pause updates until {end.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Restores default behavior by removing all the values SysManager sets: the deferral, the pause, and the pause an
    /// earlier version wrote into the policy key. Returns false if the write was denied.
    /// </summary>
    public bool RestoreDefault()
    {
        string[] names = ["DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays", .. PolicyPauseValues];
        var policy = Write(PolicyPath, key =>
        {
            foreach (var name in names)
            {
                try { key.DeleteValue(name, throwOnMissingValue: false); }
                catch (System.Security.SecurityException) { /* best-effort per-value */ }
            }
        }, "restore default update policy");
        return ClearSettingsPause() && policy;
    }

    private bool ClearPolicyPause() => ClearIfPresent(PolicyPath, PolicyPauseValues, "clear the pause in the update policy");

    private bool ClearSettingsPause() =>
        ClearIfPresent(SettingsPath, [.. SettingsPauseStarts, .. SettingsPauseEnds], "clear the paused updates");

    /// <summary>Deletes <paramref name="names"/> from the key at <paramref name="path"/>, creating nothing when it is absent.</summary>
    private bool ClearIfPresent(string path, string[] names, string what)
    {
        try
        {
            using var key = _baseKey.OpenSubKey(path, writable: true);
            if (key is null) return true;
            var present = names.Where(name => key.GetValue(name) is not null).ToList();
            foreach (var name in present) key.DeleteValue(name, throwOnMissingValue: false);
            if (present.Count > 0) Log.Information("WU policy: {What}", what);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
    }

    private bool Write(string path, Action<RegistryKey> mutate, string what)
    {
        try
        {
            using var key = _baseKey.CreateSubKey(path, writable: true);
            if (key is null) return false;
            mutate(key);
            Log.Information("WU policy: {What}", what);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
    }

    private static string Format(DateTime utc) => utc.ToString(SettingsTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>A Settings app pause time, in local time, or null when it is absent or no time.</summary>
    /// <remarks>
    /// Read in any ISO form, not only the one written here, since another tool, or another Windows, may write the same
    /// value another way, and a pause in effect that the tab did not show is the defect this reads to avoid.
    /// </remarks>
    private static DateTime? SettingsTime(RegistryKey settings, string name) =>
        settings.GetValue(name) is string s
        && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc.ToLocalTime()
            : null;

    /// <summary>The date a pause in the policy starts on, read from the first ten characters, or null.</summary>
    private static DateTime? PolicyPauseStart(RegistryKey policy, string name) =>
        policy.GetValue(name) is string { Length: >= 10 } s
        && DateTime.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start
            : null;

    private static DateTime? Later(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
