// SysManager · ProcessDescriptionService — built-in process description database
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// Safety level for a process — indicates whether it's a known Windows component,
/// a trusted third-party application, or unknown.
/// </summary>
public enum ProcessSafety
{
    /// <summary>Known Windows system component — safe, do not kill.</summary>
    System,

    /// <summary>Well-known third-party application — generally safe.</summary>
    Trusted,

    /// <summary>Not in the database — unknown origin.</summary>
    Unknown
}

/// <summary>
/// A process description entry from the built-in database.
/// </summary>
public sealed record ProcessDescriptionEntry(
    string Name,
    string Description,
    string Category,
    ProcessSafety Safety)
{
    /// <summary>
    /// What the program is called, from the start of <see cref="Description"/>: "Google Chrome" from "Google Chrome — web
    /// browser". The whole description when it has no dash to end the name at.
    /// </summary>
    /// <remarks>
    /// Every entry in the database is written as the name, a spaced em dash, then what the program does, and
    /// <c>ProcessDescriptionServiceTests</c> holds the database to that shape. The "Why is it slow?" check names a busy
    /// program by it, because "chrome is using 61%" names a file and "Google Chrome is using 61%" names the program.
    /// </remarks>
    public string ShortName
    {
        get
        {
            var end = Description.IndexOf(" — ", StringComparison.Ordinal);
            return end > 0 ? Description[..end] : Description;
        }
    }
}

/// <summary>
/// Loads and queries the built-in process description database (JSON embedded resource).
/// Thread-safe singleton — loaded once on first access.
/// </summary>
public sealed class ProcessDescriptionService
{
    private static readonly Lazy<ProcessDescriptionService> _instance = new(() => new ProcessDescriptionService());
    public static ProcessDescriptionService Instance => _instance.Value;

    private readonly Dictionary<string, ProcessDescriptionEntry> _db;

    private ProcessDescriptionService()
    {
        _db = LoadDatabase();
    }

    /// <summary>
    /// Looks up a process by name (case-insensitive, without .exe extension).
    /// Returns null if not found in the database.
    /// </summary>
    public ProcessDescriptionEntry? Lookup(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        return _db.TryGetValue(name.ToLowerInvariant(), out var entry) ? entry : null;
    }

    /// <summary>
    /// Looks up a process by name, then verifies a "System" database entry against where the image
    /// actually runs from. A process named like a known Windows component but running outside
    /// <c>%SystemRoot%</c> is not that component — the database match is downgraded to an unrecognised
    /// entry naming the real folder, instead of handing out the Windows component's description and
    /// "safe to end" reputation (see <see cref="Helpers.ProcessCriticality"/>, #2792).
    /// </summary>
    /// <remarks>
    /// Only "System" entries are re-verified: "Trusted" third-party entries have no fixed install
    /// folder to check against, and <see cref="ProcessSafety.Unknown"/> already carries no reputation
    /// to misattribute. <paramref name="imagePath"/> null/blank/unreadable passes through unchanged,
    /// the same fail-open rule <see cref="Helpers.ProcessCriticality.IsUnderSystemRoot"/> applies, since
    /// most real system processes cannot be read without elevation.
    /// </remarks>
    public ProcessDescriptionEntry? Lookup(string processName, string? imagePath)
    {
        var entry = Lookup(processName);
        if (entry is null || entry.Safety != ProcessSafety.System)
            return entry;

        if (ProcessCriticality.IsUnderSystemRoot(imagePath))
            return entry;

        var folder = Path.GetDirectoryName(imagePath) is { Length: > 0 } dir ? dir : "an unexpected location";
        return entry with
        {
            Safety = ProcessSafety.Unknown,
            Description = $"Not recognised — named like a Windows component, but running from {folder} instead of the System folder."
        };
    }

    /// <summary>
    /// Gets the plain-language description for a process, or empty string if unknown.
    /// </summary>
    public string GetDescription(string processName)
        => Lookup(processName)?.Description ?? "";

    /// <summary>
    /// Gets the category for a process, or "Unknown" if not in the database.
    /// </summary>
    public string GetCategory(string processName)
        => Lookup(processName)?.Category ?? "Unknown";

    /// <summary>
    /// Gets the safety level for a process.
    /// </summary>
    public ProcessSafety GetSafety(string processName)
        => Lookup(processName)?.Safety ?? ProcessSafety.Unknown;

    /// <summary>
    /// Gets all known categories in the database.
    /// </summary>
    public IReadOnlyList<string> GetCategories()
        => _db.Values.Select(e => e.Category).Distinct().OrderBy(c => c).ToList();

    /// <summary>
    /// Total number of entries in the database.
    /// </summary>
    public int Count => _db.Count;

    private static Dictionary<string, ProcessDescriptionEntry> LoadDatabase()
    {
        var db = new Dictionary<string, ProcessDescriptionEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ProcessDescriptions.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                Log.Warning("ProcessDescriptions.json embedded resource not found");
                return db;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Log.Warning("Could not open ProcessDescriptions.json stream");
                return db;
            }

            var entries = JsonSerializer.Deserialize<List<JsonEntry>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (entries is null) return db;

            foreach (var e in entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
            {
                var safety = e.Safety?.ToLowerInvariant() switch
                {
                    "system" => ProcessSafety.System,
                    "trusted" => ProcessSafety.Trusted,
                    _ => ProcessSafety.Unknown
                };

                var entry = new ProcessDescriptionEntry(
                    e.Name,
                    e.Description ?? "",
                    e.Category ?? "Unknown",
                    safety);

                db[e.Name.ToLowerInvariant()] = entry;
            }

            Log.Information("Process description database loaded: {Count} entries", db.Count);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse ProcessDescriptions.json");
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to read ProcessDescriptions.json");
        }

        return db;
    }

    private sealed class JsonEntry
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string Safety { get; set; } = "";
    }
}
