// SysManager · ProfileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
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
    /// The set of config files a profile carries — logical key, label, file name, and which
    /// AppData base it lives under. The base MUST match the owning service or export/import
    /// silently reads/writes the wrong location.
    /// </summary>
    private static readonly (string Key, string DisplayName, string FileName, Base Base)[] Catalog =
    [
        ("theme", "Theme & appearance", "theme.json", Base.Roaming),        // ThemeService → Roaming
        ("speedtest", "Speed-test history", "speedtest-history.json", Base.Local), // SpeedTestHistoryService → Local
    ];

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
        foreach (var (key, display, fileName, baseDir) in Catalog)
        {
            var path = Path.Combine(DirFor(baseDir), fileName);
            if (!File.Exists(path)) continue;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException ex) { Log.Debug("Profile: skipping {File} ({Error})", fileName, ex.Message); continue; }
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
            // Always use the catalog's own file name + base — never a path from the
            // (untrusted) profile — and write to the SAME folder the owning service reads.
            var dir = DirFor(known.Base);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, known.FileName);
            try
            {
                File.WriteAllText(path, section.Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                applied++;
            }
            catch (IOException ex) { Log.Warning("Profile: could not write {File}: {Error}", known.FileName, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warning("Profile: access denied writing {File}: {Error}", known.FileName, ex.Message); }
        }
        Log.Information("Profile: applied {Count} config section(s)", applied);
        return applied;
    }
}
