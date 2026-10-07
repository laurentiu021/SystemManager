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
/// <param name="PreviousStartType">The startup type to restore — one of
/// <see cref="ServiceManagerService.RestorableStartTypes"/>: "Automatic", "Automatic (Delayed Start)" or "Manual".</param>
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
/// startup types Enable can restore — <see cref="ServiceManagerService.RestorableStartTypes"/> — are
/// stored; anything else is dropped on read, because attempting a restore that cannot succeed is
/// worse than falling back to the conservative default.</para>
/// </summary>
public sealed class ServiceStartupLedgerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>
    /// Serializes <see cref="Remember"/> against <see cref="Forget"/>. Both are a <see cref="Load"/>
    /// followed by a <see cref="Persist"/> over one file holding every service's record, so each
    /// writes back a whole-dictionary snapshot. <see cref="AtomicFile"/> makes each WRITE atomic but
    /// not the read-then-write pair: without this, whichever saves last persists a snapshot taken
    /// before the other landed, and the other service's record is silently gone.
    /// <para>Nothing reachable today overlaps them — both callers are per-row commands on the
    /// Services tab that resume on the dispatcher thread, so the UI thread serializes them by
    /// accident rather than by design. This makes it a property of the service instead, because what
    /// a dropped record costs is exactly what this class exists to prevent: Enable falls back to
    /// Manual through <c>StartTypeToScToken</c>'s default while reporting success. A bulk
    /// "disable selected services" action iterating with <c>Task.WhenAll</c> is an obvious addition
    /// to that tab, and it must not be the thing that discovers this.</para>
    /// </summary>
    private readonly Lock _mutateLock = new();

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public ServiceStartupLedgerService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "service-startup-ledger.json");
    }

    /// <summary>
    /// Loads the ledger, keyed by service name, or null when the file is there and could not be read. Never throws.
    /// </summary>
    /// <remarks>
    /// Null is not "no records". A failed read used to load as an empty ledger, so Enable restored every service it
    /// could not see as Manual, and the next <see cref="Remember"/> wrote a ledger holding only the service it
    /// recorded over all the others (#2521). A file that does not parse holds no record that could be restored, so it
    /// loads as empty, and <see cref="Remember"/> sets it aside instead of writing over it.
    /// </remarks>
    public IReadOnlyDictionary<string, ServiceStartupRecord>? Load() => Read().Records;

    /// <summary>The records, null when the file could not be read, and whether it was there but did not parse.</summary>
    /// <remarks>
    /// Internal for Undo Changes, which must not call a ledger that did not parse "nothing to put back" (#1525): the
    /// services it recorded are still off.
    /// </remarks>
    internal (IReadOnlyDictionary<string, ServiceStartupRecord>? Records, bool Unparsable) Read()
    {
        var text = StoreFile.ReadText(_path);
        if (text is null) return (null, false);
        return TryParse(text) is { } ledger ? (ledger, false) : (EmptyLedger, true);
    }

    /// <summary>
    /// Records that <paramref name="serviceName"/> was <paramref name="previousStartType"/> before
    /// being disabled. A type Windows would not accept is not recorded, so Enable falls back to the
    /// conservative default instead of attempting an invalid restore.
    /// <para>Serialized against <see cref="Forget"/> by <see cref="_mutateLock"/>. The validation
    /// above it is not: rejecting a bad argument reads nothing shared.</para>
    /// </summary>
    /// <returns>
    /// False only when the ledger could not be read or written, and then the caller must not make the change: a
    /// ledger that could not be read is not written over, because it holds how every other service was set. True
    /// when the record reached the file, or there was nothing to record.
    /// </returns>
    public bool Remember(string serviceName, string? previousStartType, DateTimeOffset disabledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return true;
        if (!ServiceManagerService.IsRestorable(previousStartType))
        {
            Log.Debug("Not recording an unrestorable startup type for {Service}: {Type}",
                serviceName, previousStartType ?? "(null)");
            return true;
        }

        lock (_mutateLock)
        {
            var (records, unparsable) = Read();
            if (records is null) return false;
            if (unparsable && !StoreFile.SetAside(_path)) return false;

            var ledger = new Dictionary<string, ServiceStartupRecord>(records, StringComparer.OrdinalIgnoreCase)
            {
                [serviceName] = new(serviceName, previousStartType, disabledAtUtc)
            };
            return Persist(ledger);
        }
    }

    /// <summary>
    /// Removes a service's record — called after a successful Enable restores it. Serialized against
    /// <see cref="Remember"/> by <see cref="_mutateLock"/>.
    /// </summary>
    public void Forget(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return;

        lock (_mutateLock)
        {
            // A ledger that could not be read is left as it is: there is no copy of the other records to write back.
            if (Read().Records is not { } records) return;
            var ledger = new Dictionary<string, ServiceStartupRecord>(records, StringComparer.OrdinalIgnoreCase);
            if (!ledger.Remove(serviceName)) return;
            Persist(ledger);
        }
    }

    /// <summary>
    /// The startup type to restore for a service, or null when nothing is recorded or the ledger could not be read.
    /// A caller that must tell those two apart uses <see cref="Load"/>.
    /// </summary>
    public string? PreviousStartTypeFor(string serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && Load() is { } ledger && ledger.TryGetValue(serviceName, out var record)
            ? record.PreviousStartType
            : null;

    private bool Persist(IReadOnlyDictionary<string, ServiceStartupRecord> ledger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(ledger));
            return true;
        }
        catch (IOException ex) { Log.Debug("Service ledger save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Service ledger save denied: {Error}", ex.Message); }
        return false;
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
    public static IReadOnlyDictionary<string, ServiceStartupRecord> Parse(string? json) => TryParse(json) ?? EmptyLedger;

    /// <summary><see cref="Parse"/>, except that input which is not a ledger at all is null rather than empty.</summary>
    internal static IReadOnlyDictionary<string, ServiceStartupRecord>? TryParse(string? json)
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
                if (!ServiceManagerService.IsRestorable(record.PreviousStartType)) continue;
                ledger[record.ServiceName] = record;
            }
            return ledger;
        }
        catch (JsonException ex) { Log.Debug("Service ledger parse failed: {Error}", ex.Message); return null; }
    }
}
