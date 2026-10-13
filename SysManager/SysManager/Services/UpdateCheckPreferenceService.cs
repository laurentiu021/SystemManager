// SysManager · UpdateCheckPreferenceService — the two checks SysManager makes on its own when it starts
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The user's answers to "check for a new version when SysManager starts?" and "check winget for app updates when
/// SysManager starts?", plus when the last version check actually ran.
/// </summary>
/// <param name="CheckOnStartup">
/// Whether the startup check may run at all. Defaults to true: an update check is how a user on
/// an unsigned, self-distributed build learns about a security fix, so silence is the worse
/// default — but it is now a choice the user can see and reverse.
/// </param>
/// <param name="LastCheckUtc">
/// When the last successful startup check ran, or null if never. Drives the throttle.
/// </param>
/// <param name="CheckAppUpdatesOnStartup">
/// Whether the Dashboard's app-updates alert asks winget when SysManager starts (#2613). Defaults to true, which is
/// what it always did before it had a switch: the alert is the one people find useful, and it adds nothing about
/// them. Off, the alert says it did not check, and Scan system, Update All Apps and the App Updates tab still ask
/// winget when they are pressed. Kept beside the version check, so Profile Export / Import carries both.
/// </param>
public sealed record UpdateCheckPreference(bool CheckOnStartup, DateTimeOffset? LastCheckUtc,
    bool CheckAppUpdatesOnStartup = true);

/// <summary>
/// Persists whether the startup update check runs, and when it last ran, and whether the Dashboard asks winget for
/// app updates at startup (#2613) — the two checks SysManager makes on its own, both switched from About.
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

    /// <summary>
    /// Serializes <see cref="RecordCheck"/>, <see cref="SetCheckOnStartup"/> and <see cref="SetCheckAppUpdatesOnStartup"/>.
    /// Each is a <see cref="Load"/> followed by a <see cref="Save"/> over one file, and all three run on the one
    /// instance <see cref="ViewModels.AboutViewModel"/> holds: the startup check records itself from
    /// a continuation once the GitHub calls return, while the user's ticks arrive on the UI thread.
    /// <see cref="AtomicFile"/> makes each WRITE atomic but not the read-then-write pair, so without
    /// this whichever saves last persists a snapshot taken before the other landed — and when that
    /// is the recorder, the "off" the user just chose silently comes back on. The Dashboard holds an
    /// instance of its own and only reads, which an atomic write keeps whole.
    /// </summary>
    private readonly Lock _mutateLock = new();

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
    /// <remarks>
    /// <c>CheckAppUpdatesOnStartup</c> is nullable because files written before it existed do not have it, and those
    /// mean "on": the app-update check ran at every launch until it had a switch (#2613).
    /// </remarks>
    private sealed record Stored(bool CheckOnStartup, DateTimeOffset? LastCheckUtc, bool? CheckAppUpdatesOnStartup);

    /// <summary>
    /// Loads the preference: <see cref="Default"/> when nothing is saved yet, or null when a file is there and the
    /// choice in it could not be read, because the file could not be opened or does not parse.
    /// </summary>
    /// <remarks>
    /// Null is not "on". Both cases used to load as the default, so the startup check asked GitHub for the newest
    /// version for someone who had switched it off (#2614). <see cref="VolumePresetService"/> loads a file that does
    /// not parse as holding no presets, because none is safe to apply. The default here is a request to GitHub, so it
    /// does not stand in for a choice that could not be read.
    /// </remarks>
    public UpdateCheckPreference? Load()
    {
        var (preference, unparsable) = Read();
        return unparsable ? null : preference;
    }

    /// <summary>
    /// The stored preference, or null when the file is there and could not be read. Unparsable is true when it was
    /// read and holds no preference, and the preference is then <see cref="Default"/>.
    /// </summary>
    private (UpdateCheckPreference? Preference, bool Unparsable) Read()
    {
        var text = StoreFile.ReadText(_path);
        if (text is null) return (null, false);
        return TryParse(text) is { } preference ? (preference, false) : (Default, true);
    }

    /// <summary>Saves the preference. Best-effort: an IO failure must not break the UI.</summary>
    public void Save(UpdateCheckPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(preference));
        }
        catch (IOException ex) { Log.Debug("Update-check preference save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Update-check preference save denied: {Error}", ex.Message); }
    }

    /// <summary>
    /// Records that a check just ran, keeping the user's on/off choices. Serialized against the two
    /// setters by <see cref="_mutateLock"/>.
    /// </summary>
    /// <remarks>
    /// Writes nothing over a file that could not be read. It may hold "off", and writing the default back with the
    /// new time turned the startup check on for good (#2521). A file that does not parse is set aside first.
    /// </remarks>
    public void RecordCheck(DateTimeOffset whenUtc)
    {
        lock (_mutateLock)
        {
            var (current, unparsable) = Read();
            if (current is null) return;
            if (unparsable && !StoreFile.SetAside(_path)) return;
            Save(current with { LastCheckUtc = whenUtc });
        }
    }

    /// <summary>
    /// Turns the startup check on or off, keeping the other choice and the last-checked timestamp. Serialized against
    /// the other two writers by <see cref="_mutateLock"/>.
    /// </summary>
    /// <remarks>
    /// The user's own choice is written even over a file that could not be read (#2521). See <see cref="Update"/> for
    /// what the other choice is saved as then.
    /// </remarks>
    public void SetCheckOnStartup(bool enabled) => Update(p => p with { CheckOnStartup = enabled });

    /// <summary>
    /// Turns the Dashboard's app-update check at startup on or off (#2613), keeping the version check's choice and its
    /// last-checked timestamp. Serialized against the other two writers by <see cref="_mutateLock"/>.
    /// </summary>
    public void SetCheckAppUpdatesOnStartup(bool enabled) => Update(p => p with { CheckAppUpdatesOnStartup = enabled });

    /// <summary>
    /// What the two setters save when the file could not be read or does not parse: both choices off, before the one
    /// being made is applied.
    /// </summary>
    private static readonly UpdateCheckPreference NothingReadable =
        new(CheckOnStartup: false, LastCheckUtc: null, CheckAppUpdatesOnStartup: false);

    /// <summary>
    /// Saves one of the user's choices over what the file holds.
    /// </summary>
    /// <remarks>
    /// Written even over a file that could not be read, because it is the user's own choice; one that does not parse is
    /// set aside first, when it can be (#2521). The file then holds a second choice nobody could read, and About shows
    /// both of its boxes clear for a setting it could not read, so the other choice is saved as the box shows it, off.
    /// Building on the default would have saved it on: someone turning the version check back on, while the file held
    /// "off" for the app-update check, would have turned the app-update check on without seeing it happen (#2613).
    /// </remarks>
    private void Update(Func<UpdateCheckPreference, UpdateCheckPreference> change)
    {
        lock (_mutateLock)
        {
            var (current, unparsable) = Read();
            if (unparsable) StoreFile.SetAside(_path);
            Save(change(current is null || unparsable ? NothingReadable : current));
        }
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

    /// <summary>
    /// Whether the Dashboard asks winget for app updates when SysManager starts (#2613).
    /// </summary>
    /// <remarks>
    /// A <paramref name="preference"/> of null is a file that is there and could not be read, which may hold "off", so
    /// the check does not run, as the version check does not (#2614). No throttle, unlike the version check: the alert
    /// says what this PC needs now, as it did at every launch before it had a switch.
    /// </remarks>
    public static bool ShouldCheckAppUpdatesAtStartup(UpdateCheckPreference? preference) =>
        preference?.CheckAppUpdatesOnStartup ?? false;

    /// <summary>Serializes the preference to indented JSON.</summary>
    public static string Serialize(UpdateCheckPreference preference) =>
        JsonSerializer.Serialize(
            new Stored(preference.CheckOnStartup, preference.LastCheckUtc, preference.CheckAppUpdatesOnStartup),
            JsonOptions);

    /// <summary>
    /// Parses the stored preference; returns <see cref="Default"/> for null, blank or malformed input. The startup
    /// check does not go by this: <see cref="Load"/> does not take a file that does not parse for the default.
    /// </summary>
    public static UpdateCheckPreference Parse(string? json) => TryParse(json) ?? Default;

    /// <summary><see cref="Parse"/>, except that input which is not a preference at all is null rather than the default.</summary>
    internal static UpdateCheckPreference? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null) return Default;
            return new UpdateCheckPreference(stored.CheckOnStartup, stored.LastCheckUtc,
                stored.CheckAppUpdatesOnStartup ?? true);
        }
        catch (JsonException ex) { Log.Debug("Update-check preference parse failed: {Error}", ex.Message); return null; }
    }
}
