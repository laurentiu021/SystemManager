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
/// Exports and imports SysManager's own configuration as a single portable JSON profile,
/// so a user can replicate their setup on another PC. Only SysManager's own config files
/// are included — never system state — so applying a profile just overwrites those app
/// files and is fully reversible. Each config file is read from and written to the SAME
/// folder its owning service uses: <c>theme.json</c> lives under Roaming AppData (matching
/// <see cref="ThemeService"/>) while <c>speedtest-history.json</c> lives under Local AppData
/// (matching <see cref="SpeedTestHistoryService"/>).
///
/// The base directories are constructor-injectable so the export/import logic can be unit
/// tested against a temp directory without touching the real profile.
/// </summary>
public sealed class ProfileService
{
    /// <summary>Bump when the on-disk profile shape changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

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
    public ProfileService(string? configDir = null)
    {
        _localConfigDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _roamingConfigDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager");
    }

    /// <summary>Test seam: distinct Local/Roaming bases to verify each section lands in the right one.</summary>
    internal ProfileService(string localConfigDir, string roamingConfigDir)
    {
        _localConfigDir = localConfigDir;
        _roamingConfigDir = roamingConfigDir;
    }

    private string DirFor(Base b) => b == Base.Roaming ? _roamingConfigDir : _localConfigDir;

    /// <summary>The config sections available to export (those whose file exists on disk).</summary>
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
        return sections;
    }

    /// <summary>Builds a profile from the given sections (defaults to all available).</summary>
    public ConfigProfile BuildProfile(DateTime exportedAt, IReadOnlyList<ConfigSection>? sections = null)
        => new(CurrentSchemaVersion, UpdateService.CurrentVersion.ToString(3), exportedAt)
        { Sections = sections ?? AvailableSections() };

    /// <summary>Serializes a profile to indented JSON.</summary>
    public static string Serialize(ConfigProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    /// <summary>
    /// Parses a profile from JSON. Returns null if it is not a valid profile.
    /// Throws <see cref="NotSupportedException"/> if the schema version is newer than
    /// this build understands (so the user gets a clear "update SysManager" message
    /// rather than a silently mis-applied config).
    /// </summary>
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
        return profile is { Sections: null } ? profile with { Sections = [] } : profile;
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
