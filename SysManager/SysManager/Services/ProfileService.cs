// SysManager · ProfileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Exports and imports SysManager's configuration as a single portable JSON profile, so a user
/// can replicate their setup on another PC. Each config file is read from and written to the SAME
/// folder its owning service uses: <c>theme.json</c> lives under Roaming AppData (matching
/// <see cref="ThemeService"/>) while <c>speedtest-history.json</c> lives under Local AppData
/// (matching <see cref="SpeedTestHistoryService"/>).
/// <para>One section is not a file: the Privacy &amp; Telemetry choices, read from the registry when
/// the profile is built (#1530). An import never writes them. <see cref="ReadPrivacyChoices"/> hands
/// them back, and the Privacy &amp; Telemetry tab stages them as pending changes the user reviews and
/// applies there. Everything this class itself writes is SysManager's own config, so applying a
/// profile only overwrites those app files.</para>
///
/// The base directories are constructor-injectable so the export/import logic can be unit
/// tested against a temp directory without touching the real profile.
/// </summary>
public sealed class ProfileService
{
    /// <summary>
    /// The newest profile format this build reads. Version 2 added the privacy section; a profile without
    /// one is still written as version 1, see <see cref="BuildProfile"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The format of a profile that carries config files only, which every build since profiles shipped reads.</summary>
    private const int FilesOnlySchemaVersion = 1;

    /// <summary>
    /// The key of the privacy section. Deliberately not in <see cref="Catalog"/>: it is read from the registry
    /// rather than from a file, and nothing here ever writes it.
    /// </summary>
    public const string PrivacySectionKey = "privacy";

    private const string PrivacySectionName = "Privacy & Telemetry choices";

    /// <summary>The file name the privacy section carries in a profile. No file of that name is ever written.</summary>
    private const string PrivacySectionFileName = "privacy-profile.json";

    private readonly IPrivacyService _privacy;
    private readonly string _localConfigDir;
    private readonly string _roamingConfigDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Whether a config file lives under Roaming (true) or Local (false) AppData.</summary>
    private enum Base { Local, Roaming }

    /// <summary>
    /// The set of config files a profile carries — logical key, label, file name, which AppData base it
    /// lives under, and an optional import sanitiser. The base MUST match the owning service or
    /// export/import silently reads/writes the wrong location.
    /// <para>What is DELIBERATELY absent matters as much as what is here. A profile is meant to move a
    /// user's choices to another PC, so anything that describes THIS machine is excluded on purpose:</para>
    /// <list type="bullet">
    /// <item><c>performance-snapshot.json</c> and <c>environment-backup.json</c> — undo baselines of this
    ///   machine's power plan and PATH. Importing another PC's baseline and later pressing Restore would
    ///   apply settings this machine was never on; that is the same defect class as #1954.</item>
    /// <item><c>settings-baseline.json</c> — the Settings Watchdog's record of this machine's registry.
    ///   A foreign baseline makes the Watchdog report drift that is only "a different PC".</item>
    /// <item><c>service-startup-ledger.json</c>, <c>last-crash.json</c> — undo/diagnostic state tied to
    ///   this installation's history.</item>
    /// <item><c>activity.json</c> — the local activity log. It is a record of what happened here, not a
    ///   setting, and merging two machines' histories would make it a fiction.</item>
    /// <item><c>ProcessDescriptions.json</c>, <c>icon-fetch.json</c> — bundled data and a cache.</item>
    /// <item><c>resource-history-config.json</c> — a single retention number; a whole section and a
    ///   checkbox for one integer costs the user more attention than it saves.</item>
    /// </list>
    /// </summary>
    private static readonly (string Key, string DisplayName, string FileName, Base Base,
        Func<string, string?>? OnImport)[] Catalog =
    [
        ("theme", "Theme & appearance", "theme.json", Base.Roaming, null),        // ThemeService → Roaming
        ("speedtest", "Speed-test history", "speedtest-history.json", Base.Local, null), // SpeedTestHistoryService → Local
        ("updatecheck", "Update-check preference", "update-check.json", Base.Roaming, null), // UpdateCheckPreferenceService → Roaming
        ("darkmode", "Dark-mode schedule", "darkmode-schedule.json", Base.Roaming, null), // WindowsThemeService → Roaming
        ("gaming", "Gaming profiles", "gaming-profiles.json", Base.Local, StripActiveSession), // GamingProfileService → Local
        ("volume", "Volume presets", "volume-presets.json", Base.Local, null),   // VolumePresetService → Local
        ("closebehaviour", "Close-button behaviour", "close-preference.json", Base.Local, null), // ClosePreferenceService → Local
        ("standby", "Standby-memory preference", "standby-preference.json", Base.Local, null), // StandbyPreferenceService → Local
        // AppIconService → Local. This is a consent bit, not a cache: it records whether the Bulk
        // Installer may fetch icons from the web, and it defaults to off. It was the only user
        // preference the app persists that a profile did not carry, so exporting on one PC and
        // importing on another silently reset the choice — the same shape as "updatecheck", which is
        // also a network-consent preference and has always been carried.
        ("appicons", "App-icon fetching", "icon-fetch.json", Base.Local, null),
    ];

    /// <summary>
    /// Removes <c>ActiveSession</c> from an imported gaming-profiles file. That field is the crash-recovery
    /// marker for a game session on the machine that exported it: carried over, this PC would offer to
    /// "restore" tweaks it never applied, for a game that never ran here. The user's actual profiles — the
    /// part they configured — are kept.
    /// <para>Returns <c>null</c> to skip the section when the JSON cannot be parsed, rather than writing
    /// something unreadable over a working file.</para>
    /// </summary>
    private static string? StripActiveSession(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "ActiveSession", StringComparison.OrdinalIgnoreCase))
                        continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            Log.Warning("Profile: gaming profiles section is not valid JSON, skipping it: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Creates the service. When <paramref name="configDir"/> is given (tests), BOTH bases
    /// resolve to it so the temp tree holds every section. In production the bases are the
    /// real Roaming/Local <c>SysManager</c> folders.
    /// </summary>
    /// <param name="privacy">Reads the privacy toggles the privacy section is built from and checked against.</param>
    /// <param name="configDir">The folder both bases resolve to, for tests; null for the real ones.</param>
    public ProfileService(IPrivacyService privacy, string? configDir = null)
    {
        _privacy = privacy;
        _localConfigDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _roamingConfigDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager");
    }

    /// <summary>Test seam: distinct Local/Roaming bases to verify each section lands in the right one.</summary>
    internal ProfileService(IPrivacyService privacy, string localConfigDir, string roamingConfigDir)
    {
        _privacy = privacy;
        _localConfigDir = localConfigDir;
        _roamingConfigDir = roamingConfigDir;
    }

    private string DirFor(Base b) => b == Base.Roaming ? _roamingConfigDir : _localConfigDir;

    /// <summary>
    /// The sections available to export: those whose config file exists on disk, and the privacy choices once
    /// at least one protection is on.
    /// </summary>
    public IReadOnlyList<ConfigSection> AvailableSections()
    {
        List<ConfigSection> sections = [];
        // The import sanitiser is irrelevant on the way out — a section is exported as the owning
        // service wrote it, and only sanitised when it lands on another machine.
        foreach (var (key, display, fileName, baseDir, _) in Catalog)
        {
            var path = Path.Combine(DirFor(baseDir), fileName);
            if (!File.Exists(path)) continue;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException ex) { Log.Debug("Profile: skipping {File} ({Error})", fileName, ex.Message); continue; }
            catch (UnauthorizedAccessException ex) { Log.Debug("Profile: skipping {File} (access denied: {Error})", fileName, ex.Message); continue; }
            sections.Add(new ConfigSection(key, display, fileName, json));
        }
        if (PrivacySection() is { } privacy) sections.Add(privacy);
        return sections;
    }

    /// <summary>
    /// The privacy section as the registry reads now, or null while no protection is on.
    /// </summary>
    /// <remarks>
    /// Every toggle is carried, on or off, because the section is the whole privacy posture: imported on a PC
    /// where a protection is on that the profile has off, the Privacy &amp; Telemetry tab shows that switch
    /// turning off, and the user decides. With no protection on, this PC is on Windows' own defaults and there
    /// is nothing to carry, the same way a file section is listed only once its file exists.
    /// </remarks>
    private ConfigSection? PrivacySection()
    {
        var toggles = _privacy.LoadToggles();
        if (!toggles.Any(t => t.IsEnabled)) return null;

        var choices = new PrivacyChoices
        {
            Protections = toggles.ToDictionary(t => t.Key, t => t.IsEnabled, StringComparer.Ordinal),
        };
        return new ConfigSection(PrivacySectionKey, PrivacySectionName, PrivacySectionFileName,
            JsonSerializer.Serialize(choices, JsonOptions));
    }

    /// <summary>Whether a section is the privacy choices, which an import hands on instead of writing.</summary>
    public static bool IsPrivacySection(ConfigSection section) =>
        string.Equals(section.Key, PrivacySectionKey, StringComparison.Ordinal);

    /// <summary>
    /// Builds a profile from the config files as they are on disk now: every available section, or only those
    /// whose key is in <paramref name="keys"/>.
    /// </summary>
    /// <remarks>
    /// Reads the files itself rather than taking sections a caller read earlier. The Profile tab reads the
    /// sections when it opens and lives for the whole session, and export used to write what it read then, so
    /// a theme, preset or speed test changed since was missing from the file (#2477). A key whose file no
    /// longer exists is left out, and the caller can compare the counts to say so.
    /// <para>A profile is written in the oldest format that can hold what it carries. Only the privacy section
    /// needs version 2, so a profile of config files alone stays version 1 and an older SysManager still
    /// imports it, while one carrying privacy choices is refused there with the "update SysManager" message
    /// instead of being half-imported.</para>
    /// </remarks>
    public ConfigProfile BuildProfile(DateTime exportedAt, IReadOnlyCollection<string>? keys = null)
    {
        var sections = AvailableSections();
        if (keys is not null)
            sections = [.. sections.Where(section => keys.Contains(section.Key, StringComparer.Ordinal))];
        var schema = sections.Any(IsPrivacySection) ? CurrentSchemaVersion : FilesOnlySchemaVersion;
        return new(schema, UpdateService.CurrentVersion.ToString(3), exportedAt) { Sections = sections };
    }

    /// <summary>
    /// The privacy choices an imported privacy section carries, keeping only the toggles this build knows.
    /// Returns null when the content cannot be read or names none of them, so the caller reports that rather
    /// than staging nothing.
    /// </summary>
    /// <remarks>
    /// Checked against the toggles <see cref="IPrivacyService"/> defines, as <see cref="ApplySections"/> checks a
    /// file section against <see cref="Catalog"/>: a key this build does not know is dropped and logged. Nothing
    /// in the section reaches the registry from here. The choices only say which known switch goes which way,
    /// and the registry path each switch writes comes from the toggle's own definition.
    /// </remarks>
    public PrivacyChoices? ReadPrivacyChoices(ConfigSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        PrivacyChoices? read;
        try { read = JsonSerializer.Deserialize<PrivacyChoices>(section.Json, JsonOptions); }
        catch (JsonException ex)
        {
            Log.Warning("Profile: the privacy section is not valid JSON, skipping it: {Error}", ex.Message);
            return null;
        }
        if (read?.Protections is not { Count: > 0 } protections)
        {
            Log.Warning("Profile: the privacy section names no protections, skipping it");
            return null;
        }

        var known = _privacy.LoadToggles().Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var kept = protections
            .Where(choice => known.Contains(choice.Key))
            .ToDictionary(choice => choice.Key, choice => choice.Value, StringComparer.Ordinal);
        if (kept.Count < protections.Count)
            Log.Warning("Profile: skipping {Count} privacy choice(s) this version does not know",
                protections.Count - kept.Count);
        if (kept.Count == 0) return null;

        return new PrivacyChoices { Protections = kept };
    }

    /// <summary>Serializes a profile to indented JSON.</summary>
    public static string Serialize(ConfigProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    /// <summary>
    /// Parses a profile from JSON. Returns null if it is not a valid profile.
    /// Throws <see cref="NotSupportedException"/> if the schema version is newer than
    /// this build understands (so the user gets a clear "update SysManager" message
    /// rather than a silently mis-applied config).
    /// </summary>
    /// <remarks>
    /// This is where a file someone else made enters, so a section nothing could act on is dropped here rather
    /// than met later: a null in the list, or one with no key or no content. System.Text.Json fills neither in,
    /// whatever the record declares. Left in, a null threw while the import confirmation was being built from
    /// the sections' names, and a known key with no content threw in the write, and neither is an exception the
    /// import catches.
    /// </remarks>
    public static ConfigProfile? Deserialize(string json)
    {
        ConfigProfile? profile;
        try { profile = JsonSerializer.Deserialize<ConfigProfile>(json, JsonOptions); }
        catch (JsonException) { return null; }
        if (profile is null) return null;
        if (profile.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"This profile was made by a newer version of SysManager (format v{profile.SchemaVersion}). Update SysManager to import it.");
        // Normalize a missing "Sections" property to an empty list, mirroring
        // SettingsWatchdogService.LoadBaseline's handling of BaselineSnapshot.Values.
        // The model default already covers this, but keep the guard so any future
        // construction path (or a change to the record shape) can't reintroduce the
        // NRE that ProfileViewModel.Import hit on profile.Sections.Count.
        var sections = profile.Sections ?? [];
        List<ConfigSection> usable = [.. sections.Where(s => s is { Key: not null, Json: not null })];
        if (usable.Count < sections.Count)
            Log.Warning("Profile: dropping {Count} section(s) with no key or no content", sections.Count - usable.Count);
        return profile with { Sections = usable };
    }

    /// <summary>Writes a profile to a file the user chose.</summary>
    public async Task ExportToFileAsync(string path, ConfigProfile profile, CancellationToken ct = default)
        => await File.WriteAllTextAsync(path, Serialize(profile),
               new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);

    /// <summary>Reads + parses a profile from a file.</summary>
    public async Task<ConfigProfile?> ImportFromFileAsync(string path, CancellationToken ct = default)
        => Deserialize(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));

    /// <summary>
    /// Applies the chosen sections, overwriting the matching config files. Only sections
    /// whose key is in the known <see cref="Catalog"/> are written (so a tampered profile
    /// can't drop arbitrary files), and each file lands inside the config directory.
    /// Returns the number of sections applied.
    /// </summary>
    public int ApplySections(IEnumerable<ConfigSection> sections)
    {
        var applied = 0;
        foreach (var section in sections)
        {
            var known = Array.Find(Catalog, c => c.Key == section.Key);
            if (known.Key is null)
            {
                Log.Warning("Profile: skipping unknown config section '{Key}'", section.Key);
                continue;
            }
            // Sections that carry machine-specific state are sanitised before they land. A sanitiser
            // returning null means "this content is not safe or not readable" — skip rather than write
            // something the owning service would choke on or act wrongly upon.
            var content = section.Json;
            if (known.OnImport is { } sanitize)
            {
                if (sanitize(content) is not { } cleaned)
                {
                    Log.Warning("Profile: skipping section '{Key}' — its content did not survive the "
                        + "import check", section.Key);
                    continue;
                }
                content = cleaned;
            }

            // Always use the catalog's own file name + base — never a path from the
            // (untrusted) profile — and write to the SAME folder the owning service reads.
            var dir = DirFor(known.Base);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, known.FileName);
            try
            {
                AtomicFile.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                applied++;
            }
            catch (IOException ex) { Log.Warning("Profile: could not write {File}: {Error}", known.FileName, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warning("Profile: access denied writing {File}: {Error}", known.FileName, ex.Message); }
        }
        Log.Information("Profile: applied {Count} config section(s)", applied);
        return applied;
    }
}
