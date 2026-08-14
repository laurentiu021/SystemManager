// SysManager · SpeedTestHistoryService — persists speed test results to disk
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Persists speed test results to a local JSON file so users can track
/// service degradation over time. Stores up to <see cref="MaxPerEngine"/>
/// results per engine (HTTP / Ookla), oldest entries are trimmed on save.
/// </summary>
public sealed class SpeedTestHistoryService : IDisposable
{
    public const int MaxPerEngine = 20;

    private readonly string _historyPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // FUNC-M4: Serialize all file operations to prevent concurrent SaveAsync
    // calls from racing (load-modify-save is not atomic). A SemaphoreSlim(1,1)
    // acts as an async-compatible mutex.
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    // Idempotent, and paired with the guarded releases below: this is a DI singleton disposed at
    // process exit, so a request in flight at shutdown would otherwise release a disposed gate from a
    // finally block and log an error on a clean exit.
    private bool _disposed;

    /// <summary>
    /// Creates the service. <paramref name="configDir"/> is overridable so tests exercise the real
    /// save/load/clear paths against a temp directory instead of the user's own history file — same
    /// seam as <see cref="ResourceHistoryService"/> and <see cref="ClosePreferenceService"/>.
    /// <para>The path was previously <c>static readonly</c>, which made this service impossible to
    /// test safely: <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the
    /// Win32 known-folder API and ignores the <c>LOCALAPPDATA</c> environment variable, so nothing
    /// could redirect it away from the real profile. The tests consequently wrote fabricated results
    /// into the user's live history and one of them deleted it outright.</para>
    /// </summary>
    public SpeedTestHistoryService(string? configDir = null)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _historyPath = Path.Join(dir, "speedtest-history.json");
    }

    /// <inheritdoc />
    /// <summary>
    /// Releases the gate unless <see cref="Dispose"/> has already claimed it. Releasing a disposed
    /// <see cref="SemaphoreSlim"/> throws, and every call site is a <c>finally</c> block, where that
    /// would replace a clean shutdown — or a real error — with an unhandled exception.
    /// </summary>
    private void ReleaseFilelock()
    {
        if (_disposed) return;
        try { _fileLock.Release(); }
        catch (ObjectDisposedException) { /* disposed mid-request at shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fileLock.Dispose();
    }

    /// <summary>
    /// Loads all saved results from disk. Returns empty list on any error.
    /// </summary>
    public async Task<List<SpeedTestResult>> LoadAsync(CancellationToken ct = default)
        => await LoadCoreAsync(ct).ConfigureAwait(false);

    /// <summary>Internal load without locking — called from within locked sections.</summary>
    private async Task<List<SpeedTestResult>> LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_historyPath))
                return [];

            var json = await File.ReadAllTextAsync(_historyPath, ct).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<SpeedTestHistoryEntry>>(json, JsonOpts);
            if (entries is null) return [];

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
            return [];
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse speed test history JSON");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied loading speed test history");
            return [];
        }
    }

    /// <summary>
    /// Saves a new result, appending to existing history. Trims to
    /// <see cref="MaxPerEngine"/> per engine type.
    /// <para>Returns <c>true</c> when the result reached disk and <c>false</c> when the write failed. It
    /// used to return a bare <c>Task</c> and swallow <see cref="IOException"/> into a log warning, so a
    /// transient filesystem failure discarded the user's result while every caller — and the UI — carried
    /// on as though it had been stored. That is silent data loss of the only record the Speed Test tab
    /// keeps, and it is not theoretical: it surfaced as a one-off unit-test failure during a release, where
    /// the loaded history came back with its window shifted by exactly one entry — the signature of a
    /// single dropped write — on a run whose predecessor had passed.</para>
    /// <para>The exception is still not rethrown: losing one reading must not take the tab down. But the
    /// outcome is now reported, so a caller can tell the user instead of pretending.</para>
    /// </summary>
    public async Task<bool> SaveAsync(SpeedTestResult result, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await LoadCoreAsync(ct).ConfigureAwait(false);
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

            var dir = Path.GetDirectoryName(_historyPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(entries, JsonOpts);
            await AtomicFile.WriteAllTextAsync(_historyPath, json, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to save speed test result");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied saving speed test history");
            return false;
        }
        finally
        {
            ReleaseFilelock();
        }
    }

    /// <summary>
    /// Clears history for a specific engine, or all history if engine is null.
    /// </summary>
    public async Task ClearAsync(string? engine = null, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (engine is null)
            {
                if (File.Exists(_historyPath))
                    File.Delete(_historyPath);
                return;
            }

            var all = await LoadCoreAsync(ct).ConfigureAwait(false);
            var filtered = all.Where(r => !string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                if (File.Exists(_historyPath))
                    File.Delete(_historyPath);
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
            await AtomicFile.WriteAllTextAsync(_historyPath, json, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to clear speed test history");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied clearing speed test history");
        }
        finally
        {
            ReleaseFilelock();
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
