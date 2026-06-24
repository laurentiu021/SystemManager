// SysManager · WindowsUpdatePolicyService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and writes the documented Windows Update deferral policy keys
/// (HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate). Lets the user defer feature
/// updates, pause updates for a bounded window, and restore defaults — every change is
/// fully reversible by clearing the policy values. There is intentionally NO "disable all
/// updates" option: the strongest action is a clearly-bounded pause, so a machine is never
/// left permanently unpatched.
///
/// The registry root is injectable so the logic can be unit-tested against a redirected
/// HKCU subkey without administrator rights or touching real machine policy (mirrors
/// <see cref="AppBlockerService"/>).
/// </summary>
public sealed class WindowsUpdatePolicyService
{
    // Relative path under the base key. Under HKLM this is the real Group Policy location
    // the Windows Update client honors.
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    // Max bounded pause Windows itself honors (~35 days); we never exceed it.
    public const int MaxPauseDays = 35;

    private readonly RegistryKey _baseKey;

    /// <summary>
    /// Creates the service over a registry root. Defaults to
    /// <see cref="Registry.LocalMachine"/> (real machine policy, needs admin); tests pass a
    /// redirected root (an HKCU subkey) to avoid admin and machine writes.
    /// </summary>
    public WindowsUpdatePolicyService(RegistryKey? baseKey = null)
        => _baseKey = baseKey ?? Registry.LocalMachine;

    /// <summary>Reads the current deferral policy. Never throws — returns defaults on failure.</summary>
    public WindowsUpdatePolicy Read(DateTime now)
    {
        try
        {
            using var key = _baseKey.OpenSubKey(PolicyPath);
            if (key is null) return new WindowsUpdatePolicy(false, 0, false, null);

            var defer = key.GetValue("DeferFeatureUpdates") is int d && d == 1;
            var days = key.GetValue("DeferFeatureUpdatesPeriodInDays") is int n ? n : 0;

            DateTime? pauseUntil = null;
            if (key.GetValue("PauseFeatureUpdatesEndTime") is string s &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until))
                pauseUntil = until;
            var paused = pauseUntil is { } u && u > now;

            return new WindowsUpdatePolicy(defer, days, paused, paused ? pauseUntil : null);
        }
        catch (System.Security.SecurityException ex) { Log.Debug("WU policy read denied: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("WU policy read denied: {Error}", ex.Message); }
        return new WindowsUpdatePolicy(false, 0, false, null);
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
        return Write(key =>
        {
            key.SetValue("DeferFeatureUpdates", 1, RegistryValueKind.DWord);
            key.SetValue("DeferFeatureUpdatesPeriodInDays", clamped, RegistryValueKind.DWord);
        }, $"defer feature updates {clamped}d");
    }

    /// <summary>
    /// Pauses updates for a bounded window (clamped to <see cref="MaxPauseDays"/>), after
    /// which Windows auto-resumes. Returns false if the write was denied.
    /// </summary>
    public bool PauseUpdates(int days, DateTime now)
    {
        var until = now.AddDays(ClampPauseDays(days));
        var iso = until.ToString("o", CultureInfo.InvariantCulture);
        return Write(key =>
        {
            key.SetValue("PauseFeatureUpdatesStartTime", now.ToString("o", CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue("PauseFeatureUpdatesEndTime", iso, RegistryValueKind.String);
            key.SetValue("PauseQualityUpdatesStartTime", now.ToString("o", CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue("PauseQualityUpdatesEndTime", iso, RegistryValueKind.String);
        }, $"pause updates until {until:yyyy-MM-dd}");
    }

    /// <summary>
    /// Restores default behavior by removing all the policy values SysManager sets.
    /// Returns false if the write was denied.
    /// </summary>
    public bool RestoreDefault()
    {
        return Write(key =>
        {
            foreach (var name in new[]
            {
                "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays",
                "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
                "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime"
            })
            {
                try { key.DeleteValue(name, throwOnMissingValue: false); }
                catch (System.Security.SecurityException) { /* best-effort per-value */ }
            }
        }, "restore default update policy");
    }

    private bool Write(Action<RegistryKey> mutate, string what)
    {
        try
        {
            using var key = _baseKey.CreateSubKey(PolicyPath, writable: true);
            if (key is null) return false;
            mutate(key);
            Log.Information("WU policy: {What}", what);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("WU policy write denied ({What}): {Error}", what, ex.Message); return false; }
    }
}
