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
    private const int MaxEntries = 20;
    private static readonly string FilePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysManager", "activity.json");

    private readonly Lock _lock = new();
    private List<ActivityEntry> _entries = [];

    public static ActivityLogService Instance { get; } = new();

    private ActivityLogService() => Load();

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
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            _entries = JsonSerializer.Deserialize<List<ActivityEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("ActivityLog load failed: {Error}", ex.Message);
            _entries = [];
        }
    }

    private static void Save(List<ActivityEntry> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("ActivityLog save failed: {Error}", ex.Message);
        }
    }
}
