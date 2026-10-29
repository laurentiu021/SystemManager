// SysManager · NotificationBlockerService — per-app and global toast notification control
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Mutes app notification nags using the same documented HKCU registry values the Windows
/// Settings &gt; System &gt; Notifications page writes — nothing deeper. Two levers:
/// the per-app <c>Enabled</c> DWORD under <c>Notifications\Settings\&lt;AUMID&gt;</c>
/// (the per-app switch) and the user-wide <c>PushNotifications\ToastEnabled</c> main
/// toggle (shared with <see cref="NotificationsTweak"/> in the Gaming Profile).
///
/// Deliberately NOT here: window-hooking/pop-up interception (issue #340's original idea) —
/// that would mean injecting into or manipulating other processes' windows, which is
/// invasive, fragile, and indistinguishable from malware heuristics. Muting an app's toast
/// channel is the safe, supported way Windows itself offers; everything is per-user
/// (no admin) and reversible by flipping the same switch back.
///
/// The registry root is injectable (defaulting to <see cref="Registry.CurrentUser"/>) so the
/// logic is unit-testable against a redirected subkey (mirrors <see cref="AppBlockerService"/>,
/// <see cref="WindowsUpdatePolicyService"/>).
/// </summary>
public sealed class NotificationBlockerService : INotificationBlockerService
{
    // Per-app senders live one subkey per AUMID under this path; the per-app switch in
    // Windows Settings writes an "Enabled" DWORD (0 = muted, absent/1 = allowed) there.
    internal const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    internal const string EnabledValueName = "Enabled";

    // The user-wide main toggle. This service OWNS these two strings: NotificationsTweak (Gaming
    // Profile) writes the same value and references them from here rather than declaring its own
    // copies, which is what let the two drift apart before.
    internal const string PushKeyPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    internal const string ToastValueName = "ToastEnabled";

    // Display names for AUMIDs are registered here by desktop apps that show toasts.
    private const string AumidClassesPath = @"Software\Classes\AppUserModelId";

    // How deep the walk for a sender's key goes. A desktop program with no ID of its own has an ID made of a known
    // folder and the program's path under it, as PowerShell's is
    // "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe", and a key name cannot hold a
    // backslash, so each part is a key under the one before. A program's path seldom has more than a dozen parts: the
    // limit only bounds a walk over keys that any program running as the user can write.
    private const int MaxSenderDepth = 16;

    // Windows' own housekeeping subkeys under Notifications\Settings that are not app senders.
    private static readonly HashSet<string> NonAppSubkeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Explorer.Notification", // legacy shell bucket, not a user-facing sender
    };

    // The write ledger. A profile writes ToastEnabled=0 and so does the Notifications tab, and the two
    // are byte-identical, so the registry cannot say which one produced the 0 that Gaming Profile finds at
    // revert. This counter is the discriminator: the Notifications tab increments it, Gaming Profile records
    // it at apply, and a changed value at revert means the user has since moved the switch themselves.
    //
    // On disk rather than in memory because revert can run in a LATER PROCESS: an ActiveSession left behind
    // by a run that closed mid-game is reverted on the next launch, and that is precisely when the marker
    // must still exist. One file per feature is the established shape here (close-preference.json,
    // darkmode-schedule.json, ...).
    //
    // Deliberately NOT in ProfileService.Catalog: the number only means something next to a snapshot taken
    // on the same machine, so exporting it to another PC and importing it there would make revert compare
    // against a count that never applied to it.
    internal const string WriteLedgerFileName = "notification-master-writes.json";

    private readonly RegistryKey _baseKey;
    private readonly string _ledgerPath;
    private readonly Func<Guid, string?> _knownFolderPath;

    /// <summary>
    /// Serializes <see cref="RecordMainToggleWrite"/> against itself. It is a read-increment-write over
    /// one file holding a single counter, and <see cref="AtomicFile"/> makes the WRITE atomic but not the
    /// read-then-write pair: two overlapping toggles both read N and both write N+1, so one increment is
    /// silently lost. That is the direction that costs the user something — Gaming Profile compares the
    /// count it recorded at apply against the count at revert, so an increment that never landed makes
    /// revert restore its snapshot over a change the user made themselves.
    /// <para>Fourth store to need this, after <see cref="UpdateCheckPreferenceService"/>,
    /// <see cref="ServiceStartupLedgerService"/> and <see cref="VolumePresetService"/>;
    /// <c>ArchitectureTests.EveryStoreThatReadsThenWritesTheSameFile_SerializesThePair</c> now keeps the
    /// class closed. An instance lock is the right scope because the container registers this as a
    /// singleton; the only other construction is the designer/test graph, which never runs beside it.</para>
    /// </summary>
    private readonly Lock _ledgerLock = new();

    /// <summary>
    /// Creates the service over a registry root. Defaults to <see cref="Registry.CurrentUser"/>
    /// (the real per-user notification settings); tests pass a redirected root (a disposable
    /// HKCU subkey) so reads/writes never touch the machine's real configuration.
    /// </summary>
    public NotificationBlockerService(RegistryKey? baseKey = null, string? configDir = null)
        : this(baseKey, configDir, KnownFolders.TryGetPath)
    {
    }

    /// <summary>
    /// <see cref="NotificationBlockerService(RegistryKey?, string?)"/>, with the lookup of a known folder's path a
    /// parameter, so a test, not the PC running it, decides where the folder of a program's ID is.
    /// </summary>
    internal NotificationBlockerService(RegistryKey? baseKey, string? configDir, Func<Guid, string?> knownFolderPath)
    {
        // Defaults to the signed-in account's own notification settings via AdminHelper.PerUserRegistryRoot
        // — Registry.CurrentUser itself when SysManager runs as that account, and the account signed in to
        // the console when elevated as a different administrator, whose own senders this would otherwise
        // mute instead (#2826).
        _baseKey = baseKey ?? AdminHelper.PerUserRegistryRoot();
        _ledgerPath = LedgerPath(configDir);
        _knownFolderPath = knownFolderPath;
    }

    /// <summary>Resolves the ledger file, overridable for tests exactly as the registry root is.</summary>
    private static string LedgerPath(string? configDir) =>
        Path.Combine(configDir ?? AppDataFolders.Local, WriteLedgerFileName);

    /// <summary>The stored shape. A record so the JSON stays explicit.</summary>
    private sealed record Ledger(int Writes);

    /// <summary>
    /// How many times the Notifications tab has written the main toggle on this machine: 0 when there is no
    /// ledger yet, or null when it is there and could not be read or does not parse.
    /// </summary>
    /// <remarks>
    /// Static so <see cref="NotificationsTweak"/> can read it without taking a dependency on this service,
    /// mirroring how it already reads <see cref="PushKeyPath"/> and <see cref="ToastValueName"/> from here.
    /// <para>Null, not 0, for a count that could not be read. Gaming Profile compares the count at apply with
    /// the count at revert, and takes a change for the user muting notifications themselves. A read that failed
    /// used to return 0, which differs from every real count, so the revert left notifications muted after the
    /// game when nobody had touched them (#2538). An unknown count skips the comparison and restores, which is
    /// the behaviour that shipped before this ledger existed.</para>
    /// </remarks>
    internal static int? ReadMainToggleWriteCount(string? configDir = null) =>
        ReadMainToggleWriteCountAt(LedgerPath(configDir));

    /// <summary>
    /// Records that the user-facing path just wrote the main toggle.
    /// </summary>
    /// <remarks>
    /// A failure here is logged and swallowed: the toggle itself already succeeded, and refusing the user's
    /// change because bookkeeping failed would be a worse outcome than a revert that restores when it could
    /// have held back.
    /// <para>The whole read-increment-write is inside <see cref="_ledgerLock"/>, including the
    /// <see cref="Directory.CreateDirectory(string)"/>, so the counter cannot be read by one caller while
    /// another is between its own read and its write. Not <c>async</c> and no <c>await</c>, so the lock is
    /// never held across a yield.</para>
    /// </remarks>
    private void RecordMainToggleWrite()
    {
        try
        {
            lock (_ledgerLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
                // A ledger that could not be read counts from 0, and is written over. Its number is not data: it
                // only has to change, because an unchanged count tells a revert the user never touched the
                // toggle. Leaving it unwritten would lose this write every time; writing 1 is mistaken for no
                // write only when the count at apply was exactly 1.
                var next = (ReadMainToggleWriteCountAt(_ledgerPath) ?? 0) + 1;
                // AtomicFile, like every other store here: a torn write would read back as a malformed
                // ledger, which reads as unknown and makes revert restore when it should have held back.
                AtomicFile.WriteAllText(_ledgerPath, JsonSerializer.Serialize(new Ledger(next)));
            }
        }
        catch (IOException ex) { Log.Debug("Toast write-ledger update failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Toast write-ledger update denied: {Error}", ex.Message); }
    }

    /// <summary>
    /// Reads the ledger at an explicit path, so the instance uses its own injected location: 0 for no ledger, null
    /// for one that could not be read or does not parse.
    /// </summary>
    private static int? ReadMainToggleWriteCountAt(string path)
    {
        var text = StoreFile.ReadText(path);
        if (text is null) return null;
        if (text.Length == 0) return 0;
        try { return JsonSerializer.Deserialize<Ledger>(text)?.Writes ?? 0; }
        catch (JsonException ex) { Log.Debug("Toast write-ledger malformed: {Error}", ex.Message); return null; }
    }

    /// <inheritdoc />
    public bool IsGlobalToastEnabled()
    {
        try
        {
            using var key = _baseKey.OpenSubKey(PushKeyPath);
            // Absent value = notifications on (Windows default).
            return key?.GetValue(ToastValueName) is not int v || v != 0;
        }
        catch (System.Security.SecurityException ex) { Log.Debug("ToastEnabled read denied: {Error}", ex.Message); return true; }
        catch (UnauthorizedAccessException ex) { Log.Debug("ToastEnabled read denied: {Error}", ex.Message); return true; }
    }

    /// <inheritdoc />
    public bool SetGlobalToastEnabled(bool enabled)
    {
        try
        {
            using var key = _baseKey.CreateSubKey(PushKeyPath, writable: true);
            if (enabled)
                key.DeleteValue(ToastValueName, throwOnMissingValue: false); // absent = default = on
            else
                key.SetValue(ToastValueName, 0, RegistryValueKind.DWord);
            RecordMainToggleWrite();
            Log.Information("Notifications: main toggle set to {Enabled}", enabled);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("ToastEnabled write denied: {Error}", ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("ToastEnabled write denied: {Error}", ex.Message); return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<NotificationApp> GetApps()
    {
        List<NotificationApp> apps = [];
        try
        {
            using var settings = _baseKey.OpenSubKey(SettingsPath);
            if (settings is null) return apps;

            foreach (var aumid in settings.GetSubKeyNames())
            {
                if (NonAppSubkeys.Contains(aumid)) continue;
                AddSenders(settings, aumid, depth: 1, apps);
            }
        }
        catch (System.Security.SecurityException ex) { Log.Debug("Notification senders read denied: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Notification senders read denied: {Error}", ex.Message); }
        catch (System.IO.IOException ex) { Log.Debug("Notification senders read failed: {Error}", ex.Message); }

        return apps
            .OrderByDescending(a => a.LastNotification ?? DateTime.MinValue)
            .ThenByDescending(a => a.RecentCount)
            .ToList();
    }

    /// <summary>
    /// Adds the sender whose key is <paramref name="aumid"/> under <paramref name="settings"/>, or, for a key that
    /// holds only subkeys, the senders nested under it.
    /// </summary>
    /// <remarks>
    /// A key with values, or with nothing at all, is a sender, and its ID is its path under Notifications\Settings. A
    /// key with subkeys and no values is a folder of a nested ID, and was listed as a sender of its own, named by its
    /// GUID, which every program under that folder collapsed into and whose switch Windows never reads (#2838). A
    /// sender's own subkeys are not senders.
    /// </remarks>
    private void AddSenders(RegistryKey settings, string aumid, int depth, List<NotificationApp> apps)
    {
        try
        {
            using var appKey = settings.OpenSubKey(aumid);
            if (appKey is null) return;

            if (appKey.ValueCount > 0 || appKey.SubKeyCount == 0)
            {
                apps.Add(ReadSender(appKey, aumid));
                return;
            }

            if (depth >= MaxSenderDepth) return;
            foreach (var child in appKey.GetSubKeyNames())
                AddSenders(settings, $@"{aumid}\{child}", depth + 1, apps);
        }
        catch (System.Security.SecurityException) { /* skip unreadable sender */ }
        catch (UnauthorizedAccessException) { /* skip unreadable sender */ }
        catch (System.IO.IOException) { /* skip unreadable sender */ }
    }

    private NotificationApp ReadSender(RegistryKey appKey, string aumid)
    {
        // Absent Enabled value = notifications allowed (Windows default).
        var enabled = appKey.GetValue(EnabledValueName) is not int e || e != 0;
        var count = appKey.GetValue("PeriodicNotificationCount") is int c ? c : 0;

        DateTime? last = null;
        // FILETIME (100ns ticks since 1601) stored as QWORD; registry returns it as long.
        if (appKey.GetValue("LastNotificationAddedTime") is long ft && ft > 0)
        {
            try { last = DateTime.FromFileTime(ft); }
            catch (ArgumentOutOfRangeException) { /* corrupt timestamp — leave null */ }
        }

        return new NotificationApp
        {
            Aumid = aumid,
            DisplayName = ResolveDisplayName(aumid),
            RecentCount = count,
            LastNotification = last,
            IsEnabled = enabled,
        };
    }

    /// <inheritdoc />
    public bool SetAppEnabled(string aumid, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        // AUMIDs are registry subkey names we enumerated ourselves, but the write API is public on the seam. A backslash
        // keeps the parts of an ID that is a program's path (#2838), and every part must be a key name: RegistryKey
        // collapses an empty part and drops a trailing one, which would reach another sender's key, and "." and ".."
        // are refused with them. The key must be there already; none is created.
        if (!IsSenderPath(aumid)) return false;

        try
        {
            using var settings = _baseKey.OpenSubKey(SettingsPath, writable: true);
            using var appKey = settings?.OpenSubKey(aumid, writable: true);
            if (appKey is null) return false;

            if (enabled)
                appKey.DeleteValue(EnabledValueName, throwOnMissingValue: false); // absent = default = on
            else
                appKey.SetValue(EnabledValueName, 0, RegistryValueKind.DWord);

            Log.Information("Notifications: {Aumid} set to {Enabled}", aumid, enabled);
            return true;
        }
        catch (System.Security.SecurityException ex) { Log.Warning("Notification toggle denied for {Aumid}: {Error}", aumid, ex.Message); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning("Notification toggle denied for {Aumid}: {Error}", aumid, ex.Message); return false; }
        catch (System.IO.IOException ex) { Log.Warning("Notification toggle failed for {Aumid}: {Error}", aumid, ex.Message); return false; }
    }

    // True when every part of an ID is a key name: none empty, and none "." or "..".
    internal static bool IsSenderPath(string aumid) =>
        !aumid.Contains('/') && aumid.Split('\\').All(part => part.Length > 0 && part is not ("." or ".."));

    /// <summary>
    /// Best human-readable name for an AUMID: the DisplayName a desktop app registered under
    /// <c>HKCU\Software\Classes\AppUserModelId</c>, else, for an ID made of a known folder and a program's path, the
    /// program's own description, else a prettified tail of the AUMID itself
    /// (e.g. <c>com.squirrel.slack.slack</c> → <c>Slack</c>).
    /// </summary>
    internal string ResolveDisplayName(string aumid)
    {
        try
        {
            using var reg = _baseKey.OpenSubKey($@"{AumidClassesPath}\{aumid}");
            if (reg?.GetValue("DisplayName") is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (System.Security.SecurityException) { /* fall through to prettify */ }
        catch (UnauthorizedAccessException) { /* fall through to prettify */ }

        return ProgramDescription(aumid, _knownFolderPath) ?? PrettifyAumid(aumid);
    }

    /// <summary>
    /// The description of the program a "{known folder}\path\program.exe" ID names, or null when the ID is not one,
    /// <paramref name="folderOf"/> does not resolve the folder, or the file has none. The file is only read, never run,
    /// and only inside the folder: a part such as "C:" that would root the path elsewhere names nothing.
    /// </summary>
    internal static string? ProgramDescription(string aumid, Func<Guid, string?> folderOf)
    {
        var parts = aumid.Split('\\');
        if (parts.Length < 2 || !IsSenderPath(aumid) || !Guid.TryParseExact(parts[0], "B", out var folderId))
            return null;
        if (folderOf(folderId) is not { Length: > 0 } folder) return null;

        try
        {
            // Resolved against the folder, never the current directory, so where a path lands depends on the ID alone.
            var file = Path.GetFullPath(Path.Join(parts[1..]), folder);
            if (!SystemPaths.IsInsideSubtree(file, folder) || !File.Exists(file)) return null;
            var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Derives a readable label from an AUMID when no DisplayName registration exists.
    /// Handles the common shapes: reverse-DNS (<c>com.squirrel.slack.slack</c>),
    /// packaged-app family names (<c>Microsoft.WindowsStore_8wekyb…!App</c>), and
    /// system toasts (<c>Windows.SystemToast.StartupApp</c>).
    /// </summary>
    internal static string PrettifyAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return aumid;

        // An ID that is a program's path ends in the program's file, and is named by that file without its folder or
        // extension. A file name keeps the underscores and dots the shapes below cut a family name at.
        if (aumid.Contains('\\'))
        {
            var file = aumid[(aumid.LastIndexOf('\\') + 1)..];
            return Capitalized(Path.GetFileNameWithoutExtension(file) is { Length: > 0 } name ? name : file);
        }

        var s = aumid;

        // Packaged apps: strip the "!App" entry-point suffix and the publisher-hash suffix.
        var bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        var underscore = s.IndexOf('_');
        if (underscore > 0) s = s[..underscore];

        // System toasts read better without the fixed prefix.
        const string systemToast = "Windows.SystemToast.";
        if (s.StartsWith(systemToast, StringComparison.OrdinalIgnoreCase))
            s = s[systemToast.Length..];

        // Reverse-DNS and dotted family names: the last segment is the app name
        // (com.squirrel.slack.slack → slack; Microsoft.Office.OUTLOOK.EXE.15 → keep readable tail).
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            // Numeric or "EXE"-style tails aren't names — walk back to the last wordy segment.
            var last = Array.FindLast(parts, p =>
                p.Length > 2 && !int.TryParse(p, out _) &&
                !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? parts[^1];
            s = last;
        }

        return Capitalized(s);
    }

    // The first letter capitalized, so "slack" reads as "Slack".
    private static string Capitalized(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
