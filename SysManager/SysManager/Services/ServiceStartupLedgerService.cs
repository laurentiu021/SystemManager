// SysManager · ServiceStartupLedgerService — remembers a service's startup type before SysManager disabled it
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
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

/// <summary>What became of a <see cref="ServiceStartupLedgerService.Remember"/>.</summary>
public enum ServiceRecordSave
{
    /// <summary>The record was kept, or there was nothing to record.</summary>
    Saved,

    /// <summary>The records could not be read just now, so nothing was written over them.</summary>
    NotRead,

    /// <summary>
    /// There is nowhere to keep the record that only administrators and Windows can change: the key, or the key above
    /// it, can be changed by others. Looking again will not change that.
    /// </summary>
    NotProtected,

    /// <summary>The records were read, and Windows refused the write.</summary>
    NotWritten,
}

/// <summary>A service's record, and whether only the file an earlier version kept it in names it.</summary>
/// <param name="Record">The record, or null when there is none.</param>
/// <param name="FromEarlierVersion">
/// True when the record comes from the file in the user's profile, which any program running as the user can write.
/// </param>
internal readonly record struct ServiceRecordLookup(ServiceStartupRecord? Record, bool FromEarlierVersion);

/// <summary>
/// How many records <see cref="ServiceStartupLedgerService.ForgetAll"/> took out, and how many it could not.
/// </summary>
/// <param name="FromKey">Taken out of the protected key.</param>
/// <param name="KeptInKey">
/// Still in the protected key, because this look may not change it; each is marked as no longer standing for a change.
/// </param>
/// <param name="FromFile">Taken out of the file an earlier version kept.</param>
internal readonly record struct ServiceRecordsForgotten(int FromKey, int KeptInKey, int FromFile);

/// <summary>
/// Persists the startup type each service had before SysManager disabled it, so Enable restores
/// the original rather than guessing.
/// <para>The previous type used to live in a plain property on <c>ServiceEntry</c>, and those
/// objects are rebuilt from scratch by every scan — so the memory was lost on any Refresh and on
/// app restart. <c>StartTypeToScToken</c> falls back to "demand" (Manual) for an unknown value,
/// which meant: disable an Automatic service, restart SysManager, press Enable, and it came back
/// as Manual while the status line reported success. That is a silent change to the machine's
/// configuration, which is why this needed to be durable rather than per-session.</para>
/// <para>The records are kept in <see cref="ProtectedPath"/>, a key under HKLM\SOFTWARE only administrators and
/// Windows can change, created and checked through <see cref="ProtectedMachineKey"/> as the environment variables'
/// Machine copy is. Undo Changes turns a service back on as administrator from what this names, and earlier versions
/// kept the records in a file in the user's profile, which any program running as the user can write. That file is
/// now read-only input: the Services tab's Enable still takes a startup type from it, saying where it came from,
/// and a record is taken out of it once it no longer stands for a change, but nothing is ever written into it and
/// Undo Changes never lists what only it names. Its records are not moved into the key, because a moved record would
/// carry whatever the file said.</para>
/// <para>Only the startup types Enable can restore — <see cref="ServiceManagerService.RestorableStartTypes"/> — are
/// stored; anything else is dropped on read, because attempting a restore that cannot succeed is worse than falling
/// back to the conservative default.</para>
/// </summary>
public sealed class ServiceStartupLedgerService
{
    /// <summary>The protected key, directly under HKLM\SOFTWARE.</summary>
    internal const string ProtectedPath = @"SOFTWARE\SysManagerServiceStartupLedger";

    /// <summary>The value in <see cref="ProtectedPath"/> that holds the records.</summary>
    internal const string ProtectedValueName = "Records";

    /// <summary>The most the records may take, in UTF-8 bytes; a larger value is not read.</summary>
    internal const int MaxProtectedBytes = 64 * 1024;

    /// <summary>The file earlier versions kept the records in, in the user's profile.</summary>
    internal const string LegacyFileName = "service-startup-ledger.json";

    /// <summary>
    /// The records in the protected key that a look without administrator rights found no longer standing for a change,
    /// in the user's profile.
    /// </summary>
    internal const string StaleFileName = "service-startup-turned-back-on.json";

    private const string KeyLabel = "service record";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _legacyPath;
    private readonly string _stalePath;
    private readonly RegistryKey? _machineRoot;
    private readonly bool _enforceProtection;
    private readonly Func<RegistryKey, string, RegistryKey?> _openKey;

    /// <summary>
    /// Serializes <see cref="Remember"/> against <see cref="Forget"/> and <see cref="ForgetAll"/>. Each reads the
    /// records, then writes back a whole snapshot of them. A single write is atomic, the read-then-write pair is not:
    /// without this, whichever saves last persists a snapshot taken before the other landed, and the other service's
    /// record is silently gone.
    /// <para>Nothing reachable today overlaps them — the callers are per-row commands on the Services tab that resume
    /// on the dispatcher thread, and Undo Changes' look, which takes the system-change lock first. This makes it a
    /// property of the service instead, because what a dropped record costs is exactly what this class exists to
    /// prevent: Enable falls back to Manual through <c>StartTypeToScToken</c>'s default while reporting success.</para>
    /// </summary>
    private readonly Lock _mutateLock = new();

    /// <summary>The service over HKLM and the user's profile, as the app runs it.</summary>
    /// <remarks>
    /// The machine root comes from <see cref="MachineRegistry.Root"/>, which a test run points away from HKLM, so a
    /// construction no seam scan can see never reads or writes the real records.
    /// </remarks>
    public ServiceStartupLedgerService()
        : this(configDir: null, MachineRegistry.Root, enforceProtection: true)
    {
    }

    /// <summary>The service over the given folder and machine root, for tests and the designer graph.</summary>
    /// <param name="configDir">The folder the file an earlier version kept is in; the profile's own when null.</param>
    /// <param name="machineRoot">The hive standing in for HKLM, or null for no protected key at all.</param>
    /// <param name="enforceProtection">
    /// Whether the key and its parent must be access-controlled. Always on over the real HKLM, whatever is passed.
    /// </param>
    /// <param name="openKey">Opens the protected key for reading, so a test can make the read fail.</param>
    internal ServiceStartupLedgerService(
        string? configDir, RegistryKey? machineRoot, bool enforceProtection = false,
        Func<RegistryKey, string, RegistryKey?>? openKey = null)
    {
        var dir = configDir ?? AppDataFolders.Local;
        _legacyPath = Path.Combine(dir, LegacyFileName);
        _stalePath = Path.Combine(dir, StaleFileName);
        _machineRoot = machineRoot;
        _enforceProtection = machineRoot is not null && ProtectedMachineKey.MustEnforce(machineRoot, enforceProtection);
        _openKey = openKey ?? ((root, path) => root.OpenSubKey(path));
    }

    /// <summary>
    /// Every record, keyed by service name: the protected key's, then the earlier version's file for a service the key
    /// does not name. Null when either could not be read. Never throws.
    /// </summary>
    /// <remarks>
    /// Null is not "no records". A failed read used to load as an empty ledger, so Enable restored every service it
    /// could not see as Manual, and the next <see cref="Remember"/> wrote a ledger holding only the service it
    /// recorded over all the others (#2521). Records that do not parse hold nothing that could be restored, so they
    /// load as empty.
    /// </remarks>
    public IReadOnlyDictionary<string, ServiceStartupRecord>? Load()
    {
        if (ReadProtected().Records is not { } recorded || ReadLegacy().Records is not { } legacy) return null;
        if (legacy.Count == 0) return recorded;
        var merged = new Dictionary<string, ServiceStartupRecord>(recorded, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, record) in legacy) merged.TryAdd(name, record);
        return merged;
    }

    /// <summary>
    /// The record Enable uses for <paramref name="serviceName"/>, and whether only the earlier version's file names it;
    /// null when either could not be read.
    /// </summary>
    internal ServiceRecordLookup? Lookup(string serviceName)
    {
        if (ReadProtected().Records is not { } recorded || ReadLegacy().Records is not { } legacy) return null;
        if (recorded.TryGetValue(serviceName, out var record)) return new ServiceRecordLookup(record, false);
        return legacy.TryGetValue(serviceName, out var earlier)
            ? new ServiceRecordLookup(earlier, true)
            : new ServiceRecordLookup(null, false);
    }

    /// <summary>
    /// The protected key's records, null when they could not be read, and whether they are there but cannot be used:
    /// the key or its parent can be changed by others, or the value is of another kind, too large, or does not parse.
    /// </summary>
    /// <remarks>
    /// The only records Undo Changes lists, and the only ones it must not call "nothing to put back" when it cannot use
    /// them (#1525): the services they name are still off. No machine root reads as no records.
    /// </remarks>
    internal (IReadOnlyDictionary<string, ServiceStartupRecord>? Records, bool Unusable) ReadProtected()
    {
        if (_machineRoot is null) return (EmptyLedger, false);
        try
        {
            using var key = _openKey(_machineRoot, ProtectedPath);
            if (key is null) return (EmptyLedger, false);
            if (_enforceProtection && !ProtectedMachineKey.IsTrusted(_machineRoot, key))
            {
                Log.Warning("Service ledger: its key can be changed by others, so its records are not used");
                return (EmptyLedger, true);
            }
            if (!HasValue(key, ProtectedValueName)) return (EmptyLedger, false);
            if (key.GetValueKind(ProtectedValueName) != RegistryValueKind.String
                || key.GetValue(ProtectedValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    is not string json
                || Encoding.UTF8.GetByteCount(json) > MaxProtectedBytes)
            {
                Log.Warning("Service ledger: its records are of another kind or too large, so they are not used");
                return (EmptyLedger, true);
            }
            return TryParse(json) is { } ledger ? (ledger, false) : (EmptyLedger, true);
        }
        catch (IOException ex) { Log.Warning("Service ledger: its key could not be read: {Error}", ex.Message); }
        catch (SecurityException ex)
        {
            Log.Warning("Service ledger: reading its key was refused: {Error}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("Service ledger: reading its key was refused: {Error}", ex.Message);
        }
        return (null, false);
    }

    /// <summary>
    /// The services only the earlier version's file names, which the protected key does not; empty when either could
    /// not be read.
    /// </summary>
    internal IReadOnlyList<string> EarlierVersionOnly()
    {
        if (ReadProtected().Records is not { } recorded || ReadLegacy().Records is not { } legacy) return [];
        return [.. legacy.Keys.Where(name => !recorded.ContainsKey(name))];
    }

    /// <summary>
    /// A test, read once, of whether a look without administrator rights found a record no longer standing for a
    /// change: its service was on again, and the key could not be changed then.
    /// </summary>
    /// <remarks>
    /// Matched on the time the record carries, so a later Disable, which writes a record of its own, is never hidden by
    /// an earlier mark. The marks are in the user's profile, so one can only ever hide a record, never add one.
    /// </remarks>
    internal Func<ServiceStartupRecord, bool> StaleMarks()
    {
        var marks = TryParse(StoreFile.ReadText(_stalePath)) ?? EmptyLedger;
        return record => marks.TryGetValue(record.ServiceName, out var mark)
                         && mark.DisabledAtUtc == record.DisabledAtUtc;
    }

    /// <summary>
    /// Records that <paramref name="serviceName"/> was <paramref name="previousStartType"/> before being disabled, in
    /// the protected key. A type Windows would not accept is not recorded, so Enable falls back to the conservative
    /// default instead of attempting an invalid restore.
    /// <para>Serialized against <see cref="ForgetAll"/> by <see cref="_mutateLock"/>. The validation above it is not:
    /// rejecting a bad argument reads nothing shared.</para>
    /// </summary>
    /// <returns>
    /// Anything but <see cref="ServiceRecordSave.Saved"/> means the caller must not make the change: records that could
    /// not be read are not written over, because they hold how every other service was set, and a record kept where
    /// others can change it would be no record at all.
    /// </returns>
    public ServiceRecordSave Remember(string serviceName, string? previousStartType, DateTimeOffset disabledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return ServiceRecordSave.Saved;
        if (!ServiceManagerService.IsRestorable(previousStartType))
        {
            Log.Debug("Not recording an unrestorable startup type for {Service}: {Type}",
                serviceName, previousStartType ?? "(null)");
            return ServiceRecordSave.Saved;
        }

        lock (_mutateLock)
        {
            if (_machineRoot is null) return ServiceRecordSave.NotProtected;
            var (records, unusable) = ReadProtected();
            if (records is null) return ServiceRecordSave.NotRead;

            var ledger = new Dictionary<string, ServiceStartupRecord>(records, StringComparer.OrdinalIgnoreCase)
            {
                [serviceName] = new(serviceName, previousStartType, disabledAtUtc)
            };
            try
            {
                WriteProtected(ledger, setAsideFirst: unusable);
                return ServiceRecordSave.Saved;
            }
            catch (InvalidDataException ex)
            {
                Log.Warning("Service ledger not saved: {Error}", ex.Message);
                return ServiceRecordSave.NotProtected;
            }
            catch (IOException ex) { Log.Warning("Service ledger save failed: {Error}", ex.Message); }
            catch (SecurityException ex) { Log.Warning("Service ledger save refused: {Error}", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warning("Service ledger save refused: {Error}", ex.Message); }
            return ServiceRecordSave.NotWritten;
        }
    }

    /// <summary>
    /// Removes a service's record — called after a successful Enable restores it, and when a Disable did not happen.
    /// </summary>
    public void Forget(string serviceName) => ForgetAll([serviceName]);

    /// <summary>
    /// Removes several services' records: from the protected key, and from the earlier version's file. Serialized
    /// against <see cref="Remember"/> by <see cref="_mutateLock"/>; records that could not be read are left as they
    /// are.
    /// </summary>
    /// <remarks>
    /// Undo Changes' look usually runs without the administrator rights the key needs. Then the key's records stay,
    /// each marked in the user's profile as no longer standing for a change, so they are not offered as SysManager's if
    /// something else turns their service off again (#3041), until a look with those rights takes them out.
    /// </remarks>
    internal ServiceRecordsForgotten ForgetAll(IReadOnlyCollection<string> serviceNames)
    {
        var names = serviceNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (names.Count == 0) return default;

        lock (_mutateLock)
        {
            int fromKey = 0, kept = 0, fromFile = 0;
            // Marks that could not be read, or do not parse, are null here and never written over: they hide records
            // that are stale.
            var marks = StoreFile.ReadText(_stalePath) is { } marksText ? TryParse(marksText) : null;
            if (_machineRoot is not null && ReadProtected() is ({ } recorded, false))
            {
                var stale = names.Where(recorded.ContainsKey).Select(name => recorded[name]).ToList();
                if (stale.Count > 0)
                {
                    try
                    {
                        WriteProtected(Without(recorded, names), setAsideFirst: false);
                        fromKey = stale.Count;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or SecurityException
                                                   or UnauthorizedAccessException)
                    {
                        Log.Debug("Service ledger: records kept in its key for now: {Error}", ex.Message);
                        if (marks is not null)
                        {
                            var marked = new Dictionary<string, ServiceStartupRecord>(
                                marks, StringComparer.OrdinalIgnoreCase);
                            foreach (var record in stale) marked[record.ServiceName] = record;
                            if (PersistFile(_stalePath, marked)) kept = stale.Count;
                        }
                    }
                }
            }

            // The file earlier versions kept the records in: a record may be taken out of it, never put in.
            if (ReadLegacy() is ({ } legacy, false))
            {
                var left = Without(legacy, names);
                if (left.Count < legacy.Count && PersistFile(_legacyPath, left)) fromFile = legacy.Count - left.Count;
            }

            if (fromKey > 0 && marks is not null
                && Without(marks, names) is var unmarked && unmarked.Count < marks.Count)
                PersistFile(_stalePath, unmarked);
            return new ServiceRecordsForgotten(fromKey, kept, fromFile);
        }
    }

    /// <summary>
    /// The startup type to restore for a service, or null when nothing is recorded or the records could not be read.
    /// A caller that must tell those two apart uses <see cref="Load"/>.
    /// </summary>
    public string? PreviousStartTypeFor(string serviceName) =>
        !string.IsNullOrWhiteSpace(serviceName) && Load() is { } ledger && ledger.TryGetValue(serviceName, out var record)
            ? record.PreviousStartType
            : null;

    private (IReadOnlyDictionary<string, ServiceStartupRecord>? Records, bool Unparsable) ReadLegacy()
    {
        var text = StoreFile.ReadText(_legacyPath);
        if (text is null) return (null, false);
        return TryParse(text) is { } ledger ? (ledger, false) : (EmptyLedger, true);
    }

    /// <summary>Writes the records into the protected key, keeping aside first a value it could not use.</summary>
    /// <remarks>
    /// The value set aside is never written over: the first goes to "Records.unreadable", a later one to
    /// "Records.unreadable-2" and on, as <see cref="StoreFile.SetAside"/> names a file.
    /// </remarks>
    private void WriteProtected(IReadOnlyDictionary<string, ServiceStartupRecord> ledger, bool setAsideFirst)
    {
        using var key = ProtectedMachineKey.OpenOrCreate(_machineRoot!, ProtectedPath, _enforceProtection, KeyLabel);
        if (setAsideFirst && HasValue(key, ProtectedValueName))
        {
            var aside = ProtectedValueName + ".unreadable";
            for (var n = 2; HasValue(key, aside); n++) aside = $"{ProtectedValueName}.unreadable-{n}";
            key.SetValue(aside,
                key.GetValue(ProtectedValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!,
                key.GetValueKind(ProtectedValueName));
            Log.Warning("Service ledger: its records could not be used and were kept as {Aside}", aside);
        }
        key.SetValue(ProtectedValueName, Serialize(ledger), RegistryValueKind.String);
    }

    private static bool PersistFile(string path, IReadOnlyDictionary<string, ServiceStartupRecord> ledger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, Serialize(ledger));
            return true;
        }
        catch (IOException ex) { Log.Debug("Service ledger file save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Service ledger file save denied: {Error}", ex.Message); }
        return false;
    }

    private static Dictionary<string, ServiceStartupRecord> Without(
        IReadOnlyDictionary<string, ServiceStartupRecord> records, IReadOnlyCollection<string> serviceNames)
    {
        var ledger = new Dictionary<string, ServiceStartupRecord>(records, StringComparer.OrdinalIgnoreCase);
        foreach (var name in serviceNames) ledger.Remove(name);
        return ledger;
    }

    private static bool HasValue(RegistryKey key, string name) =>
        key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);

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
