// SysManager · UpdateCheckPreferenceService — whether and when to check GitHub for a new version
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// The user's answer to "check for a new version when SysManager starts?", plus when the last
/// check actually ran.
/// </summary>
/// <param name="CheckOnStartup">
/// Whether the startup check may run at all. Defaults to true: an update check is how a user on
/// an unsigned, self-distributed build learns about a security fix, so silence is the worse
/// default — but it is now a choice the user can see and reverse.
/// </param>
/// <param name="LastCheckUtc">
/// When the last successful startup check ran, or null if never. Drives the throttle.
/// </param>
public sealed record UpdateCheckPreference(bool CheckOnStartup, DateTimeOffset? LastCheckUtc);

/// <summary>
/// Persists whether the startup update check runs, and when it last ran.
/// <para>Before this, every launch made two unconditional calls to api.github.com — one for the
/// latest release and one for the last ten — with no setting, no UI and no record of the previous
/// check. Two consequences: the product's "no telemetry, fully local" claim had a visible
/// exception the user could neither see nor switch off, and restarting the app repeatedly could
/// burn through GitHub's anonymous limit (60 requests/hour/IP, documented in
/// <see cref="UpdateService"/>) until the About tab showed an error for no real reason.</para>
/// <para>Same shape as <see cref="ClosePreferenceService"/> and <see cref="VolumePresetService"/>:
/// an overridable directory so tests never touch the real profile, pure
/// <see cref="Serialize"/>/<see cref="Parse"/> helpers, and IO that never throws — a preference
/// file that cannot be read must not stop the app from starting.</para>
/// </summary>
public sealed class UpdateCheckPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// How long a startup check is considered fresh. Manual checks ("Check for updates", "Retry")
    /// deliberately ignore this, so the user is never blocked from asking.
    /// </summary>
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(24);

    /// <summary>The file name, also registered in <see cref="ProfileService"/>'s catalog.</summary>
    internal const string FileName = "update-check.json";

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public UpdateCheckPreferenceService(string? configDir = null)
    {
        // Roaming, like theme.json: this is a stated preference that should follow the user
        // between machines, not machine-local state like a history file.
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager");
        _path = Path.Combine(dir, FileName);
    }

    /// <summary>The stored state to serialize. A record so the JSON shape stays explicit.</summary>
    private sealed record Stored(bool CheckOnStartup, DateTimeOffset? LastCheckUtc);

    /// <summary>
    /// Loads the preference. Returns the default (enabled, never checked) when nothing is saved
    /// or the file cannot be trusted — falling back to "enabled" keeps a corrupt file from
    /// silently turning update notifications off, which the user would never notice.
    /// </summary>
    public UpdateCheckPreference Load()
    {
        try
        {
            if (!File.Exists(_path)) return Default;
            return Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Update-check preference load failed: {Error}", ex.Message); return Default; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Update-check preference load denied: {Error}", ex.Message); return Default; }
    }

    /// <summary>Saves the preference. Best-effort: an IO failure must not break the UI.</summary>
    public void Save(UpdateCheckPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Serialize(preference));
        }
        catch (IOException ex) { Log.Debug("Update-check preference save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Update-check preference save denied: {Error}", ex.Message); }
    }

    /// <summary>Records that a check just ran, keeping the user's on/off choice.</summary>
    public void RecordCheck(DateTimeOffset whenUtc)
    {
        var current = Load();
        Save(current with { LastCheckUtc = whenUtc });
    }

    /// <summary>Turns the startup check on or off, keeping the last-checked timestamp.</summary>
    public void SetCheckOnStartup(bool enabled)
    {
        var current = Load();
        Save(current with { CheckOnStartup = enabled });
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Enabled, never checked — what a first run sees.</summary>
    public static UpdateCheckPreference Default { get; } = new(CheckOnStartup: true, LastCheckUtc: null);

    /// <summary>
    /// Whether the startup check should run now.
    /// <para>False when the user turned it off, or when a check already ran inside
    /// <see cref="ThrottleWindow"/>. A future-dated timestamp — a clock change, or a file copied
    /// from another machine — is treated as stale rather than trusted, so a bad clock cannot
    /// suppress update checks indefinitely.</para>
    /// </summary>
    public static bool ShouldCheckAtStartup(UpdateCheckPreference preference, DateTimeOffset nowUtc)
    {
        if (!preference.CheckOnStartup) return false;
        if (preference.LastCheckUtc is not { } last) return true;
        if (last > nowUtc) return true;                       // clock moved back / foreign file
        return nowUtc - last >= ThrottleWindow;
    }

    /// <summary>Serializes the preference to indented JSON.</summary>
    public static string Serialize(UpdateCheckPreference preference) =>
        JsonSerializer.Serialize(
            new Stored(preference.CheckOnStartup, preference.LastCheckUtc), JsonOptions);

    /// <summary>
    /// Parses the stored preference; returns <see cref="Default"/> for null, blank or malformed
    /// input. Unlike the close preference, an unreadable file falls back to ENABLED rather than
    /// to a prompt: there is nothing to ask here, and defaulting to off would quietly stop the
    /// only channel that tells the user about a fix.
    /// </summary>
    public static UpdateCheckPreference Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null) return Default;
            return new UpdateCheckPreference(stored.CheckOnStartup, stored.LastCheckUtc);
        }
        catch (JsonException ex) { Log.Debug("Update-check preference parse failed: {Error}", ex.Message); return Default; }
    }
}
