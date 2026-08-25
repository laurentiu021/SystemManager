// SysManager · DiskScanHistoryService — remembers the last Disk Analyzer scan per root
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Persists the last Disk Analyzer scan for each root to a local JSON file, so reopening the tab can
/// show what changed since last time instead of starting blank. One snapshot per root (the newest wins),
/// capped at <see cref="MaxRoots"/> roots so drilling into many folders cannot grow the file without
/// bound.
/// <para>Modelled on <see cref="SpeedTestHistoryService"/>, down to the contract: a
/// <see cref="SemaphoreSlim"/> serialises the non-atomic load-modify-write, IO failures degrade to
/// "no history" rather than throwing to the tab, and the <c>configDir</c> seam lets tests run against a
/// temp directory instead of the user's real file.</para>
/// </summary>
public sealed class DiskScanHistoryService : IDisposable
{
    /// <summary>How many distinct roots are remembered. Oldest-by-capture are trimmed on save.</summary>
    public const int MaxRoots = 20;

    /// <summary>How many folders each snapshot keeps, largest first. Bounds the per-root payload.</summary>
    public const int MaxFoldersPerRoot = 10;

    private readonly string _historyPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Serialise all file operations: load-modify-save is not atomic, so two concurrent saves would race.
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    // Paired with the guarded release below: a DI singleton disposed at process exit could otherwise
    // release a disposed gate from a finally block and log an error on a clean shutdown.
    private bool _disposed;

    /// <summary>
    /// Creates the service. <paramref name="configDir"/> is overridable so tests exercise the real
    /// save/load paths against a temp directory — the same seam as <see cref="SpeedTestHistoryService"/>.
    /// A <c>static readonly</c> path could not be redirected, because
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the Win32
    /// known-folder API and ignores the <c>LOCALAPPDATA</c> environment variable.
    /// </summary>
    public DiskScanHistoryService(string? configDir = null)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _historyPath = Path.Join(dir, "disk-scan-history.json");
    }

    private void ReleaseFileLock()
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

    /// <summary>Every remembered snapshot, newest first. Empty on any error.</summary>
    public async Task<IReadOnlyList<DiskScanSnapshot>> LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadCoreAsync(ct).ConfigureAwait(false); }
        finally { ReleaseFileLock(); }
    }

    /// <summary>
    /// The remembered snapshot for one root, or <c>null</c> if that root has never been scanned. Path
    /// comparison is normalised so a trailing separator does not create a second entry for the same
    /// folder.
    /// </summary>
    public async Task<DiskScanSnapshot?> FindAsync(string rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;
        var key = Normalize(rootPath);
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await LoadCoreAsync(ct).ConfigureAwait(false);
            return all.FirstOrDefault(s => Normalize(s.RootPath) == key);
        }
        finally { ReleaseFileLock(); }
    }

    /// <summary>
    /// Remembers <paramref name="snapshot"/> as the latest scan of its root, replacing any earlier one
    /// for the same root and trimming to <see cref="MaxRoots"/>. Returns <c>true</c> when it reached
    /// disk. Never throws to the caller — a failed write must not take the tab down — but reports the
    /// outcome so a caller need not pretend it succeeded, the same contract
    /// <see cref="SpeedTestHistoryService.SaveAsync"/> learned the hard way.
    /// </summary>
    public async Task<bool> SaveAsync(DiskScanSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = Normalize(snapshot.RootPath);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await LoadCoreAsync(ct).ConfigureAwait(false)).ToList();

            // Upsert by root: the newest scan of a folder replaces the old one rather than accumulating.
            all.RemoveAll(s => Normalize(s.RootPath) == key);

            // Bound the payload: keep only the largest folders, and only their name + size.
            snapshot.TopFolders = snapshot.TopFolders
                .OrderByDescending(f => f.SizeBytes)
                .Take(MaxFoldersPerRoot)
                .ToList();
            all.Add(snapshot);

            var trimmed = all
                .OrderByDescending(s => s.CapturedAt)
                .Take(MaxRoots)
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var json = JsonSerializer.Serialize(trimmed, JsonOpts);
            await AtomicFile.WriteAllTextAsync(_historyPath, json, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to save disk-scan history");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied saving disk-scan history");
            return false;
        }
        finally
        {
            ReleaseFileLock();
        }
    }

    /// <summary>Forgets all remembered scans. Returns <c>true</c> when the file is gone afterwards.</summary>
    public async Task<bool> ClearAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_historyPath))
                File.Delete(_historyPath);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to clear disk-scan history");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied clearing disk-scan history");
            return false;
        }
        finally
        {
            ReleaseFileLock();
        }
    }

    /// <summary>Load without locking — called only from inside a locked section.</summary>
    private async Task<IReadOnlyList<DiskScanSnapshot>> LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_historyPath)) return [];

            var json = await File.ReadAllTextAsync(_historyPath, ct).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<DiskScanSnapshot>>(json, JsonOpts);
            if (entries is null) return [];

            // A file that parses but omits TopFolders leaves that list null on the DTO; normalise so no
            // caller has to null-check a collection that is documented as never-null.
            foreach (var e in entries)
                e.TopFolders ??= [];

            return entries;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to load disk-scan history");
            return [];
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse disk-scan history JSON");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied loading disk-scan history");
            return [];
        }
    }

    /// <summary>
    /// Case-insensitive path key with any trailing separator removed, so <c>C:\Data</c> and
    /// <c>C:\Data\</c> are one root. Falls back to the raw trimmed string if the path is malformed —
    /// a bad key still matches itself, which is all the lookup needs.
    /// </summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        try { trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)); }
        catch (ArgumentException) { /* malformed path — key it by its raw text */ }
        return trimmed.ToLowerInvariant();
    }
}
