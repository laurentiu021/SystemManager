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
/// (under %LOCALAPPDATA%\SysManager) are included — never system state — so applying a
/// profile just overwrites those app files and is fully reversible.
///
/// The config directory is constructor-injectable so the export/import logic can be unit
/// tested against a temp directory without touching the real profile.
/// </summary>
public sealed class ProfileService
{
    /// <summary>Bump when the on-disk profile shape changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _configDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The set of config files a profile carries — logical key, label, and file name.</summary>
    private static readonly (string Key, string DisplayName, string FileName)[] Catalog =
    [
        ("theme", "Theme & appearance", "theme.json"),
        ("speedtest", "Speed-test history", "speedtest-history.json"),
    ];

    public ProfileService(string? configDir = null)
        => _configDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");

    /// <summary>The config sections available to export (those whose file exists on disk).</summary>
    public IReadOnlyList<ConfigSection> AvailableSections()
    {
        List<ConfigSection> sections = [];
        foreach (var (key, display, fileName) in Catalog)
        {
            var path = Path.Combine(_configDir, fileName);
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
        => new(CurrentSchemaVersion, UpdateService.CurrentVersion.ToString(3), exportedAt,
               sections ?? AvailableSections());

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
        return profile;
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
        Directory.CreateDirectory(_configDir);
        var applied = 0;
        foreach (var section in sections)
        {
            var known = Array.Find(Catalog, c => c.Key == section.Key);
            if (known.Key is null)
            {
                Log.Warning("Profile: skipping unknown config section '{Key}'", section.Key);
                continue;
            }
            // Always use the catalog's own file name — never a path from the (untrusted) profile.
            var path = Path.Combine(_configDir, known.FileName);
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
