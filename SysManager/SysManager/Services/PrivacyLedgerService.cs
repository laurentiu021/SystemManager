// SysManager · PrivacyLedgerService — remembers what a privacy switch's values held before SysManager first wrote them
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>What one registry value held before SysManager first wrote it for a privacy switch (#2778).</summary>
/// <param name="Path">The key as the switch names it, <c>HKLM\…</c> or <c>HKCU\…</c>.</param>
/// <param name="ValueName">The value in that key.</param>
/// <param name="Kind">
/// The <see cref="RegistryValueKind"/> it had, by name, or null when there was no value: turning the switch off then
/// removes it.
/// </param>
/// <param name="Data">
/// What it held, as text: the number of a DWORD or QWORD, a string as it was written, unexpanded, and the bytes of a
/// binary value in base64. Null when there was no value, and for a multi-string.
/// </param>
/// <param name="Lines">The lines of a multi-string; null for every other kind.</param>
/// <param name="RecordedAtUtc">When it was recorded, for diagnostics.</param>
public sealed record PrivacyPriorValue(
    string Path, string ValueName, string? Kind, string? Data, string[]? Lines, DateTimeOffset RecordedAtUtc)
{
    /// <summary>Where the value lives, as the ledger finds it: the key, a backslash and the value name.</summary>
    internal static string KeyOf(string path, string valueName) => path + "\\" + valueName;

    internal string Key => KeyOf(Path, ValueName);
}

/// <summary>
/// Persists what each privacy switch's values held before SysManager first wrote them, so turning a switch off puts
/// back exactly that, removing a value only when there was none (#2778).
/// </summary>
/// <remarks>
/// <para>Turning a switch off used to remove its values whatever had been there. A policy the user had set themselves,
/// <c>AllowTelemetry = 1</c> through Group Policy or another tool, was gone after turning the diagnostic data switch
/// on and off again, and Windows fell back to the Settings app's level.</para>
/// <para>Same shape as <see cref="ServiceStartupLedgerService"/>: an injectable directory, a pure Serialize and Parse,
/// file IO that never throws, a file that does not parse set aside rather than written over. The first record of a
/// value is the one kept, so writing a switch on twice, or after Windows changed the value back, still puts back what
/// was there before SysManager's first write.</para>
/// </remarks>
public sealed class PrivacyLedgerService
{
    /// <summary>The ledger's file name in the config folder. Machine-specific, so a profile never carries it.</summary>
    internal const string FileName = "privacy-ledger.json";

    private readonly string _path;

    /// <summary>
    /// Serializes <see cref="Remember"/> against <see cref="Forget"/>: both read the whole ledger and write it back,
    /// and whichever wrote last would otherwise drop the record the other added.
    /// </summary>
    private readonly Lock _mutateLock = new();

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public PrivacyLedgerService(string? configDir = null)
    {
        var dir = configDir ?? AppDataFolders.Local;
        _path = Path.Combine(dir, FileName);
    }

    /// <summary>
    /// The records, keyed by <see cref="PrivacyPriorValue.KeyOf"/>, or null when the file is there and could not be
    /// read, and whether it was there but did not parse. Never throws.
    /// </summary>
    /// <remarks>
    /// A ledger that does not parse is not "no records": the values it recorded are still SysManager's, and turning a
    /// switch off over it would have to guess what they were.
    /// </remarks>
    internal (IReadOnlyDictionary<string, PrivacyPriorValue>? Records, bool Unparsable) Read()
    {
        var text = StoreFile.ReadText(_path);
        if (text is null) return (null, false);
        return TryParse(text) is { } ledger ? (ledger, false) : (EmptyLedger, true);
    }

    /// <summary>
    /// Records each of <paramref name="priors"/> that is not recorded yet, in one write: the first record of a value
    /// is the one kept.
    /// </summary>
    /// <returns>
    /// False when the ledger could not be read or written, and then the caller must not write the switch: a ledger that
    /// could not be read is not written over, because it holds what every other switch's values were. True when the
    /// records reached the file, or every one was there already.
    /// </returns>
    public bool Remember(IReadOnlyCollection<PrivacyPriorValue> priors)
    {
        ArgumentNullException.ThrowIfNull(priors);
        lock (_mutateLock)
        {
            var (records, unparsable) = Read();
            if (records is null) return false;
            if (unparsable && !StoreFile.SetAside(_path)) return false;

            var ledger = new Dictionary<string, PrivacyPriorValue>(records, StringComparer.OrdinalIgnoreCase);
            var added = false;
            foreach (var prior in priors)
                added |= ledger.TryAdd(prior.Key, prior);
            return (!added && !unparsable) || Persist(ledger);
        }
    }

    /// <summary>
    /// Removes the records of values that were put back. A ledger that could not be read is left as it is: there is no
    /// copy of the other records to write back.
    /// </summary>
    public void Forget(IReadOnlyCollection<PrivacyPriorValue> putBack)
    {
        ArgumentNullException.ThrowIfNull(putBack);
        if (putBack.Count == 0) return;

        lock (_mutateLock)
        {
            if (Read() is not ({ } records, false)) return;
            var ledger = new Dictionary<string, PrivacyPriorValue>(records, StringComparer.OrdinalIgnoreCase);
            var removed = false;
            foreach (var prior in putBack)
                removed |= ledger.Remove(prior.Key);
            if (removed) Persist(ledger);
        }
    }

    private bool Persist(IReadOnlyDictionary<string, PrivacyPriorValue> ledger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(ledger));
            return true;
        }
        catch (IOException ex) { Log.Warning("Privacy ledger save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Privacy ledger save denied: {Error}", ex.Message); }
        return false;
    }

    // ── Reading a value and putting it back ───────────────────────────────

    /// <summary>
    /// What <paramref name="valueName"/> under <paramref name="key"/> holds now, as a record; null when it holds a kind
    /// <see cref="PutBack"/> could not write back, and then the switch must not be written.
    /// </summary>
    internal static PrivacyPriorValue? Capture(
        RegistryKey key, string path, string valueName, DateTimeOffset nowUtc)
    {
        // Unexpanded, so an expandable string is put back as it was written rather than as it reads on this account.
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return new PrivacyPriorValue(path, valueName, null, null, null, nowUtc);

        var kind = key.GetValueKind(valueName);
        return (kind, value) switch
        {
            (RegistryValueKind.DWord, int dword) => Record(dword.ToString(CultureInfo.InvariantCulture)),
            (RegistryValueKind.QWord, long qword) => Record(qword.ToString(CultureInfo.InvariantCulture)),
            (RegistryValueKind.String or RegistryValueKind.ExpandString, string text) => Record(text),
            (RegistryValueKind.MultiString, string[] lines) =>
                new PrivacyPriorValue(path, valueName, kind.ToString(), null, lines, nowUtc),
            (RegistryValueKind.Binary or RegistryValueKind.None, byte[] bytes) => Record(Convert.ToBase64String(bytes)),
            _ => null,
        };

        PrivacyPriorValue Record(string data) => new(path, valueName, kind.ToString(), data, null, nowUtc);
    }

    /// <summary>
    /// Writes <paramref name="prior"/> back under <paramref name="key"/>: removes the value when there was none, and
    /// otherwise writes the data it held with the kind it had.
    /// </summary>
    internal static void PutBack(RegistryKey key, PrivacyPriorValue prior)
    {
        if (prior.Kind is null)
        {
            key.DeleteValue(prior.ValueName, throwOnMissingValue: false);
            return;
        }

        var kind = Enum.Parse<RegistryValueKind>(prior.Kind);
        object data = kind switch
        {
            RegistryValueKind.DWord => int.Parse(prior.Data!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(prior.Data!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            RegistryValueKind.MultiString => prior.Lines!,
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(prior.Data!),
            _ => prior.Data!,
        };
        key.SetValue(prior.ValueName, data, kind);
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    private static readonly IReadOnlyDictionary<string, PrivacyPriorValue> EmptyLedger =
        new Dictionary<string, PrivacyPriorValue>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes the ledger as a JSON array of records.</summary>
    public static string Serialize(IReadOnlyDictionary<string, PrivacyPriorValue> ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return JsonSerializer.Serialize(ledger.Values.ToArray(), JsonDefaults.Indented);
    }

    /// <summary>
    /// Parses the ledger; null for input that is not a ledger at all, empty for none. A record <see cref="PutBack"/>
    /// could not write back, one with no key or value name, a kind it does not know, or data that does not fit its
    /// kind, is skipped rather than failing the file, so one bad entry cannot lose the rest; turning its switch off
    /// then removes the value, as it did before records were kept.
    /// </summary>
    internal static IReadOnlyDictionary<string, PrivacyPriorValue>? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyLedger;
        try
        {
            var records = JsonSerializer.Deserialize<PrivacyPriorValue[]>(json);
            if (records is null) return EmptyLedger;

            var ledger = new Dictionary<string, PrivacyPriorValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record is null || string.IsNullOrWhiteSpace(record.Path) || string.IsNullOrEmpty(record.ValueName))
                    continue;
                if (!CanPutBack(record)) continue;
                ledger.TryAdd(record.Key, record);
            }
            return ledger;
        }
        catch (JsonException ex) { Log.Warning("Privacy ledger parse failed: {Error}", ex.Message); return null; }
    }

    /// <summary>True when <see cref="PutBack"/> can write <paramref name="record"/> as it says.</summary>
    internal static bool CanPutBack(PrivacyPriorValue record)
    {
        if (record.Kind is null) return true;
        // Only a kind's own name: Enum.TryParse also reads a number, and a list of names as their combined value.
        if (!Enum.TryParse<RegistryValueKind>(record.Kind, ignoreCase: false, out var kind)
            || kind.ToString() != record.Kind)
            return false;

        const NumberStyles integer = NumberStyles.Integer;
        return kind switch
        {
            RegistryValueKind.DWord => int.TryParse(record.Data, integer, CultureInfo.InvariantCulture, out _),
            RegistryValueKind.QWord => long.TryParse(record.Data, integer, CultureInfo.InvariantCulture, out _),
            RegistryValueKind.String or RegistryValueKind.ExpandString => record.Data is not null,
            RegistryValueKind.MultiString => record.Lines is not null,
            RegistryValueKind.Binary or RegistryValueKind.None => IsBase64(record.Data),
            _ => false,
        };
    }

    private static bool IsBase64(string? data)
    {
        if (data is null) return false;
        Span<byte> buffer = data.Length <= 1024 ? stackalloc byte[data.Length] : new byte[data.Length];
        return Convert.TryFromBase64String(data, buffer, out _);
    }
}
