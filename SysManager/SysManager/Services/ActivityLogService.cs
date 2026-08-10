// SysManager · ActivityLogService — persists last N user actions for Dashboard history
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

public sealed class ActivityLogService
{
    /// <summary>
    /// How many entries are kept. Raised from 20 once the destructive operations started logging:
    /// at 20 a busy session could still push a Deep Cleanup or an uninstall out of the history, and
    /// this file is the only record of what the app changed. Each entry is a short action/detail pair,
    /// so the JSON stays a few kilobytes.
    /// </summary>
    internal const int MaxEntries = 60;

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private List<ActivityEntry> _entries = [];

    public static ActivityLogService Instance { get; } = new();

    private ActivityLogService() : this(null) { }

    /// <summary>
    /// Creates an instance whose store lives under <paramref name="configDir"/>. The production
    /// singleton passes null and resolves the real profile path.
    /// <para>The path was previously <c>static readonly</c>, which made this service impossible to
    /// test: <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the Win32
    /// known-folder API and ignores the <c>LOCALAPPDATA</c> environment variable, so a test calling
    /// <see cref="Log"/> would have written into the user's own activity history. That is why this
    /// seam exists — see the ratchet in ArchitectureTests and issue #1741.</para>
    /// </summary>
    internal ActivityLogService(string? configDir)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _filePath = Path.Join(dir, "activity.json");
        Load();
    }

    public IReadOnlyList<ActivityEntry> GetRecent(int count = 5)
    {
        lock (_lock)
            return _entries.Take(count).ToArray();
    }

    public void Log(string action, string detail)
    {
        var entry = new ActivityEntry(action, detail, DateTime.Now);
        List<ActivityEntry> snapshot;
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            // Take the snapshot to persist while still holding the lock — serializing
            // _entries directly (outside the lock) could race a concurrent Log() that is
            // mutating the list, throwing "collection was modified" or writing torn JSON.
            snapshot = [.. _entries];
        }
        Save(snapshot);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<ActivityEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("ActivityLog load failed: {Error}", ex.Message);
            _entries = [];
        }
    }

    // Instance method (was static) because the destination path is now per-instance — that is what
    // lets a test point the store at a temp directory instead of the user's real activity history.
    private void Save(List<ActivityEntry> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            // WriteIndented = false is the default, so no options object is needed — allocating
            // one per save would only defeat System.Text.Json's per-options metadata cache.
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("ActivityLog save failed: {Error}", ex.Message);
        }
    }
}
