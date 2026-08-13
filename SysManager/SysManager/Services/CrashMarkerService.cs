// SysManager · CrashMarkerService — records and reports an abnormal process exit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text.Json;
using Serilog;

namespace SysManager.Services;

/// <summary>What a previous run recorded on its way down.</summary>
/// <param name="WhenUtc">When the crash was recorded.</param>
/// <param name="Version">The app version that crashed, so a marker can be matched to a release.</param>
/// <param name="ExceptionType">The exception's full type name.</param>
/// <param name="Message">The exception message. No stack trace and no paths — the log has the detail.</param>
public sealed record CrashMarker(
    DateTimeOffset WhenUtc, string Version, string ExceptionType, string Message);

/// <summary>
/// Reads (and clears) the marker <c>App.OnDomain</c> writes when the process dies from an unhandled
/// exception.
/// <para>A domain-level unhandled exception kills the process with no UI at all, so nothing told the
/// user — or the next launch — that the previous session ended abnormally. The user's report becomes
/// "it just closed", with none of the evidence the app had already written to disk. This turns that
/// silent death into something the next start can surface.</para>
/// <para>Same shape as the other small persisted-state services (<see cref="VolumePresetService"/>,
/// <see cref="ClosePreferenceService"/>, <see cref="StandbyPreferenceService"/>): injectable
/// directory, pure unit-testable <see cref="Parse"/>, file IO that never throws to the caller. The
/// marker is deliberately deleted as soon as it is read, so a single crash prompts exactly once.</para>
/// </summary>
public sealed class CrashMarkerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Markers older than this are ignored. A crash the user has already moved on from is noise, and
    /// a stale marker left by a much older version tells them nothing actionable.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public CrashMarkerService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "last-crash.json");
    }

    /// <summary>
    /// Returns the previous run's crash marker and deletes it, or null when the last run exited
    /// cleanly. Never throws; a marker that cannot be read is treated as absent.
    /// </summary>
    public CrashMarker? TakePending(DateTimeOffset nowUtc)
    {
        CrashMarker? marker;
        try
        {
            if (!File.Exists(_path)) return null;
            marker = Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Crash marker read failed: {Error}", ex.Message); return null; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Crash marker read denied: {Error}", ex.Message); return null; }

        // Delete regardless of age or validity: a marker that is never cleared would prompt on every
        // launch forever, which is worse than losing one notification.
        try { File.Delete(_path); }
        catch (IOException ex) { Log.Debug("Crash marker delete failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Crash marker delete denied: {Error}", ex.Message); }

        if (marker is null) return null;
        return IsFresh(marker, nowUtc) ? marker : null;
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>
    /// True when the marker is recent enough to be worth mentioning. A future-dated marker (clock
    /// change, or a file copied from another machine) is rejected rather than trusted.
    /// </summary>
    public static bool IsFresh(CrashMarker marker, DateTimeOffset nowUtc)
    {
        var age = nowUtc - marker.WhenUtc;
        return age >= TimeSpan.Zero && age <= MaxAge;
    }

    /// <summary>Serializes a marker. Mirrors what <c>App.BuildCrashMarker</c> writes.</summary>
    public static string Serialize(CrashMarker marker) => JsonSerializer.Serialize(marker, JsonOptions);

    /// <summary>
    /// Parses a marker; returns null for blank, malformed, or incomplete input. A marker missing its
    /// timestamp is dropped, since freshness could not then be judged.
    /// </summary>
    public static CrashMarker? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<CrashMarker>(json);
            if (marker is null || marker.WhenUtc == default) return null;
            return marker;
        }
        catch (JsonException ex) { Log.Debug("Crash marker parse failed: {Error}", ex.Message); return null; }
    }

    /// <summary>
    /// The plain-language sentence shown to the user. Names no exception type and no file path: the
    /// target persona cannot act on either, and the log folder is already surfaced by the Logs tab.
    /// </summary>
    public static string DescribeForUser(CrashMarker marker) =>
        $"SysManager closed unexpectedly on {marker.WhenUtc.ToLocalTime().ToString("d MMMM, HH:mm", CultureInfo.InvariantCulture)}. " +
        "Details were saved to its log, which you can open from the System Logs tab if you want to report it.";
}
