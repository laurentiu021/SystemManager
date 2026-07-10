// SysManager · EnvironmentVariableService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and writes Windows environment variables for both the User
/// (HKCU\Environment) and Machine (HKLM ...\Session Manager\Environment) scopes.
///
/// Writes go directly to the registry so the value KIND is preserved: a variable
/// stored as REG_EXPAND_SZ (e.g. a PATH containing %SystemRoot%) stays REG_EXPAND_SZ,
/// and reads return the RAW value with %VAR% tokens intact (not the expansion). Using
/// <see cref="Environment.SetEnvironmentVariable(string,string,EnvironmentVariableTarget)"/>
/// would instead rewrite every variable as REG_SZ and freeze its tokens to their
/// edit-time expansion — silently corrupting PATH. After a batch of writes the caller
/// broadcasts WM_SETTINGCHANGE once (see <see cref="BroadcastSettingChange"/>) so
/// already-running processes (Explorer, new shells) pick the change up without a reboot.
///
/// Machine-scope writes require administrator rights; <see cref="SetVariable"/> returns
/// <c>false</c> (rather than throwing) when the write is denied, mirroring
/// <see cref="PrivacyService"/>. A timestamped JSON backup of every variable is written
/// before the first mutation so the user can fully restore the original environment.
/// </summary>
public sealed partial class EnvironmentVariableService
{
    // Registry locations of the two environment scopes.
    private const string UserEnvPath = @"Environment";
    private const string MachineEnvPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private readonly string _backupDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Creates the service. The optional <paramref name="backupDir"/> override exists
    /// for testing so the backup/restore logic can run without touching the real
    /// %LOCALAPPDATA% backup location.
    /// </summary>
    public EnvironmentVariableService(string? backupDir = null)
    {
        _backupDir = backupDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "Backups", "Environment");
    }

    // Variable names: letters, digits, underscore and a few shell-safe punctuation
    // characters; no '=', no whitespace, no control chars. Rejects the leading '='
    // used by hidden drive-current-directory pseudo-variables (=C:, =ExitCode).
    // \A…\z (absolute anchors): ^…$ would accept a trailing newline in the variable
    // name before it is used as a registry value name.
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.()\-]*\z")]
    private static partial Regex VariableNameRegex();

    /// <summary>Validates an environment-variable name. Throws <see cref="ArgumentException"/> on invalid input.</summary>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Variable name cannot be empty.", nameof(name));
        if (name.Length > 255)
            throw new ArgumentException("Variable name is too long (max 255 characters).", nameof(name));
        if (!VariableNameRegex().IsMatch(name))
            throw new ArgumentException(
                $"Invalid variable name: '{name}'. Use letters, digits and underscores; no spaces or '='.", nameof(name));
        return name.Trim();
    }

    /// <summary>
    /// Non-throwing validation for pre-existing (registry-originated) names that may not
    /// conform to the strict user-input rules. Returns <c>false</c> for names that fail
    /// validation, allowing the caller to skip/count them rather than aborting.
    /// </summary>
    public static bool TryValidateName(string name, out string validatedName)
    {
        validatedName = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 255) return false;
        if (!VariableNameRegex().IsMatch(name))
        {
            Log.Debug("Environment: skipping variable with non-conforming name: '{Name}'", name);
            return false;
        }
        validatedName = name.Trim();
        return true;
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    private static (RegistryKey hive, string path) Location(EnvVarScope scope) =>
        scope == EnvVarScope.Machine
            ? (Registry.LocalMachine, MachineEnvPath)
            : (Registry.CurrentUser, UserEnvPath);

    /// <summary>
    /// Reads all variables for the given scope, sorted by name. Reads the RAW value
    /// (<see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>) so %VAR% tokens
    /// are preserved for round-tripping, and records the value KIND so a write can keep
    /// REG_EXPAND_SZ intact.
    /// </summary>
    public List<EnvVariable> Read(EnvVarScope scope)
    {
        List<EnvVariable> result = [];
        var (hive, path) = Location(scope);
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var raw = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames);
                var expandable = key.GetValueKind(name) == RegistryValueKind.ExpandString;
                result.Add(new EnvVariable
                {
                    Name = name,
                    Scope = scope,
                    Value = raw?.ToString() ?? "",
                    IsExpandable = expandable
                });
            }
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "Environment: reading {Scope} scope denied", scope);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Environment: reading {Scope} scope denied", scope);
        }
        return [.. result.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Reads both scopes into one list (User first, then Machine).</summary>
    public List<EnvVariable> ReadAll()
    {
        List<EnvVariable> all = [.. Read(EnvVarScope.User)];
        all.AddRange(Read(EnvVarScope.Machine));
        return all;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets (or, when <paramref name="value"/> is null, deletes) a variable, writing
    /// directly to the registry so the value KIND is preserved. An existing variable
    /// keeps its kind (REG_EXPAND_SZ stays expandable); a new variable is written as
    /// REG_EXPAND_SZ when its value contains a %VAR% token, else REG_SZ. Returns
    /// <c>false</c> if the name fails validation or the write is denied — never throws
    /// for these cases, so callers (RestoreFromBackup, ApplyChanges) can count failures
    /// and continue instead of aborting.
    ///
    /// Does NOT broadcast WM_SETTINGCHANGE — the caller broadcasts once after a batch via
    /// <see cref="BroadcastSettingChange"/>.
    /// </summary>
    public bool SetVariable(string name, string? value, EnvVarScope scope)
        => SetVariable(name, value, scope, explicitKind: null);

    /// <summary>
    /// Sets (or deletes) a variable with an explicit <see cref="RegistryValueKind"/> override.
    /// When <paramref name="explicitKind"/> is non-null the variable is written as that kind,
    /// bypassing the <see cref="ChooseKind"/> heuristic — used by <see cref="RestoreFromBackup"/>
    /// to restore REG_EXPAND_SZ fidelity from the backup's recorded kind.
    /// </summary>
    public bool SetVariable(string name, string? value, EnvVarScope scope, RegistryValueKind? explicitKind)
    {
        if (!TryValidateName(name, out var validName))
        {
            Log.Warning("Environment: cannot set variable with invalid name '{Name}' in {Scope} — skipped", name, scope);
            return false;
        }
        var (hive, path) = Location(scope);
        try
        {
            using var key = hive.OpenSubKey(path, writable: true);
            if (key is null)
            {
                Log.Warning("Environment: {Scope} environment key not found", scope);
                return false;
            }

            if (value is null)
            {
                key.DeleteValue(validName, throwOnMissingValue: false);
                Log.Information("Environment: deleted {Scope} variable {Name}", scope, validName);
                return true;
            }

            var kind = explicitKind ?? ChooseKind(ExistingKind(key, validName), value);
            key.SetValue(validName, value, kind);
            Log.Information("Environment: set {Scope} variable {Name} ({Kind})", scope, validName, kind);
            return true;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "Environment: write to {Scope} variable {Name} denied (elevation required)", scope, validName);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Environment: write to {Scope} variable {Name} denied (elevation required)", scope, validName);
            return false;
        }
    }

    private static RegistryValueKind? ExistingKind(RegistryKey key, string name)
    {
        try { return key.GetValueKind(name); }
        catch (IOException) { return null; }   // value does not exist yet
    }

    /// <summary>
    /// Decides the registry value kind for a write: keep an existing variable's kind so a
    /// REG_EXPAND_SZ (e.g. PATH) is never flattened to REG_SZ; for a NEW variable use
    /// REG_EXPAND_SZ when the value contains a %VAR% token, else REG_SZ. Pure for testing.
    /// </summary>
    public static RegistryValueKind ChooseKind(RegistryValueKind? existingKind, string value)
        => existingKind ?? (value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);

    /// <summary>Deletes a variable. Returns false if the write is denied.</summary>
    public bool DeleteVariable(string name, EnvVarScope scope) => SetVariable(name, null, scope);

    /// <summary>
    /// Broadcasts WM_SETTINGCHANGE("Environment") so already-running processes pick up
    /// environment changes without a reboot. Bounded (SMTO_ABORTIFHUNG, 5 s) so a frozen
    /// top-level window can't hang the caller. Call once after a batch of writes.
    /// </summary>
    public static void BroadcastSettingChange()
    {
        try
        {
            _ = NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero, "Environment",
                NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch (EntryPointNotFoundException ex) { Log.Debug("Environment: WM_SETTINGCHANGE broadcast unavailable: {Error}", ex.Message); }
    }

    private static partial class NativeMethods
    {
        internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        internal const uint WM_SETTINGCHANGE = 0x001A;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SendMessageTimeoutW")]
        internal static partial IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
    }

    // ── PATH helpers (pure, testable) ──────────────────────────────────────────

    /// <summary>
    /// Splits a ';'-separated PATH value into trimmed, non-empty directory tokens,
    /// preserving order. (Windows ignores empty PATH segments.)
    /// </summary>
    public static List<string> SplitPath(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0)];

    /// <summary>Joins directories back into a ';'-separated PATH value.</summary>
    public static string JoinPath(IEnumerable<string> directories) =>
        string.Join(';', directories.Select(d => d.Trim()).Where(d => d.Length > 0));

    /// <summary>
    /// Removes duplicate directories (case-insensitive, ignoring a trailing '\'),
    /// keeping the first occurrence and preserving order. Returns the deduplicated list.
    /// </summary>
    public static List<string> Deduplicate(IEnumerable<string> directories)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            var key = dir.TrimEnd('\\', '/');
            if (seen.Add(key)) result.Add(dir);
        }
        return result;
    }

    // ── Backup / restore ───────────────────────────────────────────────────────

    /// <summary>Path of the pristine pre-SysManager backup (created before the first write).</summary>
    public string BackupPath => Path.Combine(_backupDir, "environment-backup.json");

    /// <summary>True if a pristine backup already exists.</summary>
    public bool HasBackup => File.Exists(BackupPath);

    /// <summary>
    /// Writes a one-time pristine backup of every User and Machine variable, so the
    /// original environment can be restored later. No-op if a backup already exists
    /// (preserving the truly-original snapshot, like <see cref="HostsFileService"/>).
    /// </summary>
    public void EnsureBackup()
    {
        if (HasBackup) return;
        Directory.CreateDirectory(_backupDir);
        var userVars = Read(EnvVarScope.User);
        var machineVars = Read(EnvVarScope.Machine);
        var snapshot = new EnvBackup(
            User: ToDict(userVars),
            Machine: ToDict(machineVars),
            UserKinds: ToKindDict(userVars),
            MachineKinds: ToKindDict(machineVars));
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshot, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.Information("Environment: pristine backup written to {Path}", BackupPath);
    }

    private static Dictionary<string, string> ToDict(IEnumerable<EnvVariable> vars)
    {
        Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vars) dict[v.Name] = v.Value;
        return dict;
    }

    private static Dictionary<string, RegistryValueKind> ToKindDict(IEnumerable<EnvVariable> vars)
    {
        Dictionary<string, RegistryValueKind> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vars)
            dict[v.Name] = v.IsExpandable ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        return dict;
    }

    /// <summary>
    /// A point-in-time snapshot of both environment scopes. The Kind dictionaries record
    /// each variable's <see cref="RegistryValueKind"/> so a restore round-trips
    /// REG_EXPAND_SZ faithfully instead of relying on the '%' heuristic. Nullable for
    /// backward compatibility with backups written before this field was added.
    /// </summary>
    public sealed record EnvBackup(
        Dictionary<string, string> User,
        Dictionary<string, string> Machine,
        Dictionary<string, RegistryValueKind>? UserKinds = null,
        Dictionary<string, RegistryValueKind>? MachineKinds = null);

    /// <summary>Reads the backup snapshot, or null if there is none / it cannot be parsed.</summary>
    public EnvBackup? ReadBackup()
    {
        if (!HasBackup) return null;
        try
        {
            return JsonSerializer.Deserialize<EnvBackup>(File.ReadAllText(BackupPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Environment: backup file is corrupt and will be ignored");
            return null;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Environment: backup file could not be read");
            return null;
        }
    }

    /// <summary>The outcome of a <see cref="RestoreFromBackup"/> call.</summary>
    public readonly record struct RestoreResult(bool HadBackup, int Restored, int Removed, int Failed);

    /// <summary>
    /// Restores the environment to the pristine backup: every backed-up variable is
    /// written back (kind preserved for existing names), and any variable that did NOT
    /// exist in the backup is removed. Returns counts. Machine-scope changes need admin —
    /// those writes count as failures (not exceptions) when not elevated. The caller
    /// should <see cref="BroadcastSettingChange"/> once afterwards.
    /// </summary>
    public RestoreResult RestoreFromBackup()
    {
        var backup = ReadBackup();
        if (backup is null) return new RestoreResult(false, 0, 0, 0);

        int restored = 0, removed = 0, failed = 0;
        foreach (var scope in new[] { EnvVarScope.User, EnvVarScope.Machine })
        {
            var saved = scope == EnvVarScope.Machine ? backup.Machine : backup.User;
            var kinds = scope == EnvVarScope.Machine ? backup.MachineKinds : backup.UserKinds;

            foreach (var (name, value) in saved)
            {
                RegistryValueKind? explicitKind = kinds is not null && kinds.TryGetValue(name, out var savedKind)
                    ? savedKind
                    : null;
                if (SetVariable(name, value, scope, explicitKind)) restored++;
                else failed++;
            }

            foreach (var current in Read(scope))
            {
                if (saved.ContainsKey(current.Name)) continue;
                if (DeleteVariable(current.Name, scope)) removed++;
                else failed++;
            }
        }
        Log.Information("Environment: restored {Restored}, removed {Removed}, failed {Failed} from backup", restored, removed, failed);
        return new RestoreResult(true, restored, removed, failed);
    }
}
