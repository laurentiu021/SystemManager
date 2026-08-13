// SysManager · ServiceStartupLedgerService — remembers a service's startup type before SysManager disabled it
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// What a service's startup type was before SysManager disabled it.
/// </summary>
/// <param name="ServiceName">The service's short name (the key Windows uses).</param>
/// <param name="PreviousStartType">The startup type to restore — "Automatic", "Manual", "Boot", or "System".</param>
/// <param name="DisabledAtUtc">When SysManager disabled it, for diagnostics.</param>
public sealed record ServiceStartupRecord(
    string ServiceName, string PreviousStartType, DateTimeOffset DisabledAtUtc);

/// <summary>
/// Persists the startup type each service had before SysManager disabled it, so Enable restores
/// the original rather than guessing.
/// <para>The previous type used to live in a plain property on <c>ServiceEntry</c>, and those
/// objects are rebuilt from scratch by every scan — so the memory was lost on any Refresh and on
/// app restart. <c>StartTypeToScToken</c> falls back to "demand" (Manual) for an unknown value,
/// which meant: disable an Automatic service, restart SysManager, press Enable, and it came back
/// as Manual while the status line reported success. That is a silent change to the machine's
/// configuration, which is why this needed to be durable rather than per-session.</para>
/// <para>Same shape as <see cref="VolumePresetService"/> and <see cref="ClosePreferenceService"/>:
/// injectable directory, pure unit-testable Serialize/Parse, file IO that never throws. Only the
/// four startup types Windows accepts are stored; anything else is dropped on read, because
/// restoring a value the OS would reject is worse than falling back to the conservative default.</para>
/// </summary>
public sealed class ServiceStartupLedgerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Startup types that may be restored. "Disabled" is deliberately absent — restoring a service
    /// to Disabled is what Enable exists to undo — and an unrecognised value is not trusted.
    /// </summary>
    private static readonly HashSet<string> RestorableTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Automatic", "Manual", "Boot", "System" };

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public ServiceStartupLedgerService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "service-startup-ledger.json");
    }

    /// <summary>Loads the ledger, keyed by service name. Never throws; returns empty on any problem.</summary>
    public IReadOnlyDictionary<string, ServiceStartupRecord> Load()
    {
        try
        {
            if (!File.Exists(_path)) return EmptyLedger;
            return Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Service ledger load failed: {Error}", ex.Message); return EmptyLedger; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Service ledger load denied: {Error}", ex.Message); return EmptyLedger; }
    }

    /// <summary>
    /// Records that <paramref name="serviceName"/> was <paramref name="previousStartType"/> before
    /// being disabled. A type Windows would not accept is not recorded, so Enable falls back to the
    /// conservative default instead of attempting an invalid restore.
    /// </summary>
    public void Remember(string serviceName, string? previousStartType, DateTimeOffset disabledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return;
        if (previousStartType is null || !RestorableTypes.Contains(previousStartType))
        {
            Log.Debug("Not recording an unrestorable startup type for {Service}: {Type}",
                serviceName, previousStartType ?? "(null)");
            return;
        }

        var ledger = new Dictionary<string, ServiceStartupRecord>(Load(), StringComparer.OrdinalIgnoreCase)
        {
            [serviceName] = new(serviceName, previousStartType, disabledAtUtc)
        };
        Persist(ledger);
    }

    /// <summary>Removes a service's record — called after a successful Enable restores it.</summary>
    public void Forget(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return;

        var ledger = new Dictionary<string, ServiceStartupRecord>(Load(), StringComparer.OrdinalIgnoreCase);
        if (!ledger.Remove(serviceName)) return;
        Persist(ledger);
    }

    /// <summary>The startup type to restore for a service, or null when nothing is recorded.</summary>
    public string? PreviousStartTypeFor(string serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && Load().TryGetValue(serviceName, out var record)
            ? record.PreviousStartType
            : null;

    private void Persist(IReadOnlyDictionary<string, ServiceStartupRecord> ledger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(ledger));
        }
        catch (IOException ex) { Log.Debug("Service ledger save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Service ledger save denied: {Error}", ex.Message); }
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    private static readonly IReadOnlyDictionary<string, ServiceStartupRecord> EmptyLedger =
        new Dictionary<string, ServiceStartupRecord>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes the ledger as a JSON array of records, newest write last.</summary>
    public static string Serialize(IReadOnlyDictionary<string, ServiceStartupRecord> ledger) =>
        JsonSerializer.Serialize(ledger.Values.ToArray(), JsonOptions);

    /// <summary>
    /// Parses the ledger; returns empty for null, blank, or malformed input. Individual records
    /// missing a name or carrying a startup type Windows would reject are skipped rather than
    /// failing the whole file, so one bad entry cannot lose the rest of the ledger.
    /// </summary>
    public static IReadOnlyDictionary<string, ServiceStartupRecord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyLedger;
        try
        {
            var records = JsonSerializer.Deserialize<ServiceStartupRecord[]>(json);
            if (records is null) return EmptyLedger;

            var ledger = new Dictionary<string, ServiceStartupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record is null) continue;
                if (string.IsNullOrWhiteSpace(record.ServiceName)) continue;
                if (!RestorableTypes.Contains(record.PreviousStartType ?? "")) continue;
                ledger[record.ServiceName] = record;
            }
            return ledger;
        }
        catch (JsonException ex) { Log.Debug("Service ledger parse failed: {Error}", ex.Message); return EmptyLedger; }
    }
}
