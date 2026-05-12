// SysManager · SpeedTestHistoryService — persists speed test results to disk
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Persists speed test results to a local JSON file so users can track
/// service degradation over time. Stores up to <see cref="MaxPerEngine"/>
/// results per engine (HTTP / Ookla), oldest entries are trimmed on save.
/// </summary>
public sealed class SpeedTestHistoryService
{
    public const int MaxPerEngine = 20;

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysManager", "speedtest-history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Loads all saved results from disk. Returns empty list on any error.
    /// </summary>
    public async Task<List<SpeedTestResult>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(HistoryPath))
                return new List<SpeedTestResult>();

            var json = await File.ReadAllTextAsync(HistoryPath, ct).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<SpeedTestHistoryEntry>>(json, JsonOpts);
            if (entries == null) return new List<SpeedTestResult>();

            return entries.Select(e => new SpeedTestResult(
                e.Engine ?? "HTTP",
                e.DownloadMbps,
                e.UploadMbps,
                e.PingMs,
                e.Server ?? "",
                e.CompletedAt)).ToList();
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to load speed test history");
            return new List<SpeedTestResult>();
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse speed test history JSON");
            return new List<SpeedTestResult>();
        }
    }

    /// <summary>
    /// Saves a new result, appending to existing history. Trims to
    /// <see cref="MaxPerEngine"/> per engine type.
    /// </summary>
    public async Task SaveAsync(SpeedTestResult result, CancellationToken ct = default)
    {
        try
        {
            var all = await LoadAsync(ct).ConfigureAwait(false);
            all.Add(result);

            // Trim per engine: keep only the most recent MaxPerEngine entries.
            var trimmed = all
                .GroupBy(r => r.Engine, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(r => r.CompletedAt).Take(MaxPerEngine))
                .OrderByDescending(r => r.CompletedAt)
                .ToList();

            var entries = trimmed.Select(r => new SpeedTestHistoryEntry
            {
                Engine = r.Engine,
                DownloadMbps = r.DownloadMbps,
                UploadMbps = r.UploadMbps,
                PingMs = r.PingMs,
                Server = r.Server,
                CompletedAt = r.CompletedAt
            }).ToList();

            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(entries, JsonOpts);
            await File.WriteAllTextAsync(HistoryPath, json, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to save speed test result");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied saving speed test history");
        }
    }

    /// <summary>
    /// Clears history for a specific engine, or all history if engine is null.
    /// </summary>
    public async Task ClearAsync(string? engine = null, CancellationToken ct = default)
    {
        try
        {
            if (engine == null)
            {
                if (File.Exists(HistoryPath))
                    File.Delete(HistoryPath);
                return;
            }

            var all = await LoadAsync(ct).ConfigureAwait(false);
            var filtered = all.Where(r => !string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                if (File.Exists(HistoryPath))
                    File.Delete(HistoryPath);
                return;
            }

            var entries = filtered.Select(r => new SpeedTestHistoryEntry
            {
                Engine = r.Engine,
                DownloadMbps = r.DownloadMbps,
                UploadMbps = r.UploadMbps,
                PingMs = r.PingMs,
                Server = r.Server,
                CompletedAt = r.CompletedAt
            }).ToList();

            var json = JsonSerializer.Serialize(entries, JsonOpts);
            await File.WriteAllTextAsync(HistoryPath, json, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to clear speed test history");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied clearing speed test history");
        }
    }

    /// <summary>JSON-serializable DTO for history entries.</summary>
    private sealed class SpeedTestHistoryEntry
    {
        public string? Engine { get; set; }
        public double DownloadMbps { get; set; }
        public double UploadMbps { get; set; }
        public double PingMs { get; set; }
        public string? Server { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
