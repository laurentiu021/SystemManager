// SysManager · AppAlertHistory — New App Alerts' detections, kept between sessions
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>The detections kept, newest first, or that the record could not be read.</summary>
public sealed record AppAlertsRead(IReadOnlyList<AppInstallEntry> Alerts, bool Readable)
{
    public static readonly AppAlertsRead Unreadable = new([], false);
}

/// <summary>
/// What New App Alerts noticed being installed, with the time it noticed, kept so it outlives the session (#1507).
/// </summary>
/// <remarks>
/// The list used to live only in the tab's memory: closing SysManager lost every detection, and Recent Changes could
/// not say when a program had appeared. Clear History on New App Alerts clears this too, since the two are one list.
/// </remarks>
public interface IAppAlertHistory
{
    /// <summary>The detections kept, newest first.</summary>
    AppAlertsRead Load();

    /// <summary>Keeps one more detection.</summary>
    void Add(AppInstallEntry alert);

    /// <summary>Marks every detection kept as looked at.</summary>
    void AcknowledgeAll();

    /// <summary>Forgets every detection. Returns false when the record could not be removed.</summary>
    bool Clear();
}

/// <summary>The record behind <see cref="IAppAlertHistory"/>, in <c>app-alerts.json</c>.</summary>
public sealed class AppAlertHistory : IAppAlertHistory
{
    /// <summary>How many detections are kept, newest first. A PC gaining 200 programs unasked has bigger problems.</summary>
    internal const int MaxAlerts = 200;

    private readonly string _filePath;
    private readonly Lock _lock = new();

    /// <summary>The history production uses, in the user's profile.</summary>
    public AppAlertHistory() : this(null) { }

    /// <summary>A history kept under <paramref name="configDir"/>, so a test points it at a temp folder.</summary>
    internal AppAlertHistory(string? configDir)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _filePath = Path.Join(dir, "app-alerts.json");
    }

    /// <summary>The file the detections are kept in.</summary>
    internal string FilePath => _filePath;

    public AppAlertsRead Load()
    {
        lock (_lock)
        {
            var stored = ReadStore();
            return stored is null ? AppAlertsRead.Unreadable : new AppAlertsRead([.. stored.Select(ToEntry)], true);
        }
    }

    public void Add(AppInstallEntry alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        lock (_lock)
        {
            if (ReadStore() is not { } stored) return;
            Save([FromEntry(alert), .. stored.Take(MaxAlerts - 1)]);
        }
    }

    public void AcknowledgeAll()
    {
        lock (_lock)
        {
            if (ReadStore() is not { } stored || stored.All(a => a.IsAcknowledged)) return;
            Save([.. stored.Select(a => a with { IsAcknowledged = true })]);
        }
    }

    public bool Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("App alert history not cleared: {Error}", ex.Message);
                return false;
            }
        }
    }

    /// <summary>One detection as the file holds it.</summary>
    internal sealed record StoredAlert(string Name, string Publisher, string InstallPath, DateTime DetectedAt, string Source,
                                       bool IsAcknowledged);

    /// <summary>
    /// The detections in the file: empty when there is no file yet, null when it is there and could not be read. A file
    /// that does not parse is set aside, so the next detection starts a new one rather than being refused for good.
    /// </summary>
    private List<StoredAlert>? ReadStore()
    {
        var json = StoreFile.ReadText(_filePath);
        if (json is null) return null;
        if (json.Length == 0) return [];
        if (Parse(json) is { } alerts) return alerts;
        return StoreFile.SetAside(_filePath) ? [] : null;
    }

    /// <summary>The detections in <paramref name="json"/>, or null when it is not a list of them.</summary>
    internal static List<StoredAlert>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<StoredAlert>>(json)?
                .Where(a => a is { Name.Length: > 0 })
                .Select(a => a with { Publisher = a.Publisher ?? "", InstallPath = a.InstallPath ?? "", Source = a.Source ?? "" })
                .ToList();
        }
        catch (JsonException ex)
        {
            Log.Debug("App alert history unreadable: {Error}", ex.Message);
            return null;
        }
    }

    private void Save(List<StoredAlert> alerts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(alerts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("App alert history not saved: {Error}", ex.Message);
        }
    }

    private static AppInstallEntry ToEntry(StoredAlert a) => new()
    {
        Name = a.Name,
        Publisher = a.Publisher,
        InstallPath = a.InstallPath,
        DetectedAt = a.DetectedAt,
        Source = a.Source,
        IsAcknowledged = a.IsAcknowledged,
    };

    private static StoredAlert FromEntry(AppInstallEntry e) =>
        new(e.Name, e.Publisher ?? "", e.InstallPath ?? "", e.DetectedAt, e.Source ?? "", e.IsAcknowledged);
}
