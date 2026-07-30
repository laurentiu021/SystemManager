// SysManager · EnvironmentVariableService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
/// <see cref="PrivacyService"/>. New backups are stored in their matching registry hive:
/// User state under HKCU and Machine state under access-controlled HKLM. Legacy
/// LocalAppData backups remain read-only compatibility input for User restore only.
/// </summary>
public sealed partial class EnvironmentVariableService
{
    // Registry locations of the two environment scopes.
    internal const string UserEnvPath = @"Environment";
    internal const string MachineEnvPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    internal const string UserBackupPath = @"SOFTWARE\SysManager\Backups\Environment";
    internal const string UserBackupValueName = "Snapshot";
    internal const string MachineBackupPath = @"SOFTWARE\SysManagerEnvironmentBackup";
    internal const string MachineBackupValueName = "Snapshot";
    internal const int MaxBackupFileBytes = 1024 * 1024;
    internal const int MaxVariableCount = 4096;
    internal const int MaxVariableNameLength = 16383;
    internal const int MaxVariableValueLength = 32767;

    private readonly string _backupDir;
    private readonly RegistryKey _userRoot;
    private readonly RegistryKey _machineRoot;
    private readonly bool _enforceMachineBackupProtection;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 8,
        AllowDuplicateProperties = false
    };

    /// <summary>
    /// Creates the service. The optional <paramref name="backupDir"/> override exists
    /// for testing so the backup/restore logic can run without touching the real
    /// %LOCALAPPDATA% backup location.
    /// </summary>
    public EnvironmentVariableService(string? backupDir = null)
        : this(
            backupDir,
            Registry.CurrentUser,
            Registry.LocalMachine,
            enforceMachineBackupProtection: true)
    {
    }

    internal EnvironmentVariableService(
        string? backupDir,
        RegistryKey userRoot,
        RegistryKey machineRoot,
        bool enforceMachineBackupProtection = false)
    {
        _backupDir = backupDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "Backups", "Environment");
        _userRoot = userRoot;
        _machineRoot = machineRoot;
        _enforceMachineBackupProtection = enforceMachineBackupProtection;
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

    private static bool TryValidateBackupName(string? name, out string validatedName)
    {
        validatedName = "";
        if (string.IsNullOrEmpty(name) ||
            name.Length > MaxVariableNameLength ||
            name.Contains('\0'))
        {
            return false;
        }

        validatedName = name;
        return true;
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    private (RegistryKey hive, string path) Location(EnvVarScope scope) =>
        scope == EnvVarScope.Machine
            ? (_machineRoot, MachineEnvPath)
            : (_userRoot, UserEnvPath);

    /// <summary>
    /// Reads all variables for the given scope, sorted by name. Reads the RAW value
    /// (<see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>) so %VAR% tokens
    /// are preserved for round-tripping, and records the value KIND so a write can keep
    /// REG_EXPAND_SZ intact.
    /// </summary>
    public List<EnvVariable> Read(EnvVarScope scope) =>
        ReadCore(scope, requireKey: false, requireSupportedKinds: false);

    private List<EnvVariable> ReadCore(
        EnvVarScope scope,
        bool requireKey,
        bool requireSupportedKinds)
    {
        List<EnvVariable> result = [];
        var (hive, path) = Location(scope);
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
            {
                if (requireKey)
                    throw new IOException($"The {scope} environment registry key is unavailable.");
                return result;
            }
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var raw = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = key.GetValueKind(name);
                if (requireSupportedKinds &&
                    kind is not RegistryValueKind.String and not RegistryValueKind.ExpandString)
                {
                    throw new InvalidDataException(
                        $"The {scope} environment contains an unsupported registry value kind.");
                }
                if (requireSupportedKinds && raw is not string)
                {
                    throw new InvalidDataException(
                        $"The {scope} environment contains a non-string registry value.");
                }
                result.Add(new EnvVariable
                {
                    Name = name,
                    Scope = scope,
                    Value = raw?.ToString() ?? "",
                    IsExpandable = kind == RegistryValueKind.ExpandString
                });
            }
        }
        catch (System.Security.SecurityException ex) when (!requireKey)
        {
            Log.Warning(ex, "Environment: reading {Scope} scope denied", scope);
        }
        catch (UnauthorizedAccessException ex) when (!requireKey)
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

            var existingKind = ExistingKind(key, validName);
            if (explicitKind is null &&
                existingKind is not null and not RegistryValueKind.String and not RegistryValueKind.ExpandString)
            {
                Log.Warning(
                    "Environment: cannot safely overwrite {Scope} variable {Name} with unsupported kind {Kind}",
                    scope,
                    validName,
                    existingKind);
                return false;
            }

            var kind = explicitKind ?? ChooseKind(existingKind, value);
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
        => existingKind is RegistryValueKind.String or RegistryValueKind.ExpandString
            ? existingKind.Value
            : value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;

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

    /// <summary>Path of the legacy read-only User backup used by earlier releases.</summary>
    public string BackupPath => Path.Combine(_backupDir, "environment-backup.json");

    /// <summary>True when any backup artifact exists, including one that needs repair.</summary>
    public bool HasBackup => ReadUserBackup().Exists || ReadMachineBackup().Exists;

    /// <summary>True when a validated User snapshot exists.</summary>
    public bool HasUserBackup => ReadUserBackup().Snapshot is not null;

    /// <summary>True when a validated machine-protected snapshot exists.</summary>
    public bool HasMachineBackup => ReadMachineBackup().Snapshot is not null;

    /// <summary>
    /// Writes independent one-time snapshots before a scope's first mutation. New User
    /// snapshots are stored under HKCU and Machine snapshots under access-controlled HKLM.
    /// A legacy LocalAppData Machine section is never promoted across that boundary.
    /// </summary>
    public void EnsureBackup() => EnsureBackup(includeUser: true, includeMachine: true);

    /// <summary>Ensures pristine snapshots only for the scopes about to be changed.</summary>
    public void EnsureBackup(bool includeUser, bool includeMachine)
    {
        // Validate every present artifact even when this operation will not mutate that
        // scope. Otherwise Apply could create a snapshot that the all-or-nothing restore
        // contract can never consume.
        var userBackup = ReadUserBackup();
        var machineBackup = ReadMachineBackup();

        if (userBackup.IsInvalid || machineBackup.IsInvalid)
            throw new InvalidDataException(
                "An existing environment backup is invalid and will not be replaced.");

        string? userJson = null;
        string? machineJson = null;
        if (includeUser && !userBackup.Exists)
        {
            var snapshot = CaptureScope(EnvVarScope.User);
            userJson = SerializeBounded(new UserEnvBackup(snapshot.Values, snapshot.Kinds));
        }
        if (includeMachine && !machineBackup.Exists)
        {
            var snapshot = CaptureScope(EnvVarScope.Machine);
            machineJson = SerializeBounded(new MachineEnvBackup(snapshot.Values, snapshot.Kinds));
        }

        // Publish only after every requested missing scope has been captured and validated.
        if (machineJson is not null)
            WriteMachineBackup(machineJson);
        if (userJson is not null)
            WriteUserBackup(userJson);
    }

    private void WriteUserBackup(string json)
    {
        using var key = _userRoot.CreateSubKey(UserBackupPath, writable: true)
            ?? throw new UnauthorizedAccessException("The User backup key could not be created.");
        key.SetValue(UserBackupValueName, json, RegistryValueKind.String);
        Log.Information("Environment: pristine User backup written to HKCU storage");
    }

    private void WriteMachineBackup(string json)
    {
        using var key = OpenOrCreateMachineBackupKey();
        key.SetValue(MachineBackupValueName, json, RegistryValueKind.String);
        Log.Information("Environment: pristine Machine backup written to protected registry storage");
    }

    private RegistryKey OpenOrCreateMachineBackupKey()
    {
        if (!_enforceMachineBackupProtection)
        {
            return _machineRoot.CreateSubKey(MachineBackupPath, writable: true)
                ?? throw new UnauthorizedAccessException(
                    "The protected Machine backup key could not be created.");
        }

        EnsureMachineBackupParentIsProtected();
        using (var key = _machineRoot.CreateSubKey(
                   MachineBackupPath,
                   RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryOptions.None,
                   CreateProtectedMachineBackupSecurity())
               ?? throw new UnauthorizedAccessException(
                   "The protected Machine backup key could not be created."))
        {
            if (!IsProtectedMachineBackupSecurity(key.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)))
            {
                throw new InvalidDataException(
                    "The Machine backup key is not protected from standard-user writes.");
            }
        }

        return _machineRoot.OpenSubKey(MachineBackupPath, writable: true)
            ?? throw new UnauthorizedAccessException(
                "The protected Machine backup key could not be reopened.");
    }

    private void EnsureMachineBackupParentIsProtected()
    {
        if (!IsMachineBackupParentProtected())
        {
            throw new InvalidDataException(
                "The Machine backup parent key is not protected from standard-user writes.");
        }
    }

    private bool IsMachineBackupParentProtected()
    {
        using var parent = _machineRoot.OpenSubKey(
            "SOFTWARE",
            RegistryRights.ReadKey | RegistryRights.ReadPermissions);
        return parent is not null && IsProtectedMachineBackupSecurity(parent.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));
    }

    internal static RegistrySecurity CreateProtectedMachineBackupSecurity()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);

        RegistrySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new RegistryAccessRule(
            administrators,
            RegistryRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            system,
            RegistryRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            users,
            RegistryRights.ReadKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static bool IsProtectedMachineBackupSecurity(RegistrySecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !IsTrustedMachinePrincipal(owner) ||
            !security.AreAccessRulesCanonical)
            return false;

        const RegistryRights mutatingRights =
            RegistryRights.SetValue |
            RegistryRights.CreateSubKey |
            RegistryRights.CreateLink |
            RegistryRights.Delete |
            RegistryRights.ChangePermissions |
            RegistryRights.TakeOwnership;

        foreach (RegistryAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.RegistryRights & mutatingRights) == 0 ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            var principal = (SecurityIdentifier)rule.IdentityReference;
            if (!IsTrustedMachinePrincipal(principal) &&
                !principal.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrustedMachinePrincipal(SecurityIdentifier principal) =>
        principal.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private ScopeBackup CaptureScope(EnvVarScope scope)
    {
        var variables = ReadCore(
            scope,
            requireKey: true,
            requireSupportedKinds: true);
        if (scope == EnvVarScope.Machine && variables.Count == 0)
            throw new InvalidDataException("The Machine environment is empty and cannot be backed up safely.");
        if (variables.Count > MaxVariableCount)
            throw new InvalidDataException($"The {scope} environment contains too many variables to back up safely.");

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RegistryValueKind> kinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            if (!TryValidateBackupName(variable.Name, out var name))
                throw new InvalidDataException($"The {scope} environment contains an unsupported variable name.");
            if (variable.Value.Length > MaxVariableValueLength)
                throw new InvalidDataException($"The {scope} environment contains an oversized variable value.");
            if (!values.TryAdd(name, variable.Value))
                throw new InvalidDataException($"The {scope} environment contains duplicate variable names.");

            kinds.Add(
                name,
                variable.IsExpandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }

        return new ScopeBackup(values, kinds);
    }

    private static string SerializeBounded<T>(T snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxBackupFileBytes)
            throw new InvalidDataException("The environment backup exceeds the supported size.");
        return json;
    }

    /// <summary>
    /// A User-scope snapshot. New snapshots live under HKCU; old LocalAppData files may
    /// contain Machine fields, but deserialization deliberately ignores those fields.
    /// </summary>
    public sealed record EnvBackup(
        Dictionary<string, string> User,
        Dictionary<string, string> Machine,
        Dictionary<string, RegistryValueKind>? UserKinds = null,
        Dictionary<string, RegistryValueKind>? MachineKinds = null);

    private sealed record UserEnvBackup(
        Dictionary<string, string> User,
        Dictionary<string, RegistryValueKind>? UserKinds = null);

    private sealed record MachineEnvBackup(
        Dictionary<string, string> Machine,
        Dictionary<string, RegistryValueKind>? MachineKinds = null);

    private sealed record ScopeBackup(
        Dictionary<string, string> Values,
        Dictionary<string, RegistryValueKind>? Kinds);

    private readonly record struct ScopeRestoreCounts(int Restored, int Removed, int Failed)
    {
        public ScopeRestoreCounts Add(ScopeRestoreCounts other) => new(
            Restored + other.Restored,
            Removed + other.Removed,
            Failed + other.Failed);
    }

    private readonly record struct BackupRead<T>(bool Exists, T? Snapshot)
        where T : class
    {
        public bool IsInvalid => Exists && Snapshot is null;
        public static BackupRead<T> Missing => new(false, null);
        public static BackupRead<T> Invalid => new(true, null);
        public static BackupRead<T> Valid(T snapshot) => new(true, snapshot);
    }

    /// <summary>
    /// Reads validated authoritative snapshots in the legacy aggregate shape. Legacy
    /// LocalAppData Machine fields are never included; Machine data comes only from HKLM.
    /// Returns null if a present artifact is invalid or no snapshot exists.
    /// </summary>
    public EnvBackup? ReadBackup()
    {
        var userBackup = ReadUserBackup();
        var machineBackup = ReadMachineBackup();
        if (userBackup.IsInvalid || machineBackup.IsInvalid)
            return null;
        if (userBackup.Snapshot is null && machineBackup.Snapshot is null)
            return null;

        return new EnvBackup(
            userBackup.Snapshot?.User ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            machineBackup.Snapshot?.Machine ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            userBackup.Snapshot?.UserKinds,
            machineBackup.Snapshot?.MachineKinds);
    }

    private BackupRead<UserEnvBackup> ReadUserBackup()
    {
        var registryBackup = ReadUserRegistryBackup();
        return registryBackup.Exists
            ? registryBackup
            : ReadLegacyUserBackup();
    }

    private BackupRead<UserEnvBackup> ReadUserRegistryBackup()
    {
        try
        {
            using var key = _userRoot.OpenSubKey(UserBackupPath);
            if (key is null || !key.GetValueNames().Contains(
                    UserBackupValueName,
                    StringComparer.OrdinalIgnoreCase))
            {
                return BackupRead<UserEnvBackup>.Missing;
            }

            var rawValue = key.GetValue(
                UserBackupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (rawValue is not string json ||
                key.GetValueKind(UserBackupValueName) != RegistryValueKind.String)
            {
                Log.Warning("Environment: User backup has an invalid registry type");
                return BackupRead<UserEnvBackup>.Invalid;
            }

            return ParseUserBackup(json, "User registry");
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Environment: User registry backup is corrupt");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Environment: User registry backup could not be read");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "Environment: User registry backup read was denied");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Environment: User registry backup read was denied");
            return BackupRead<UserEnvBackup>.Invalid;
        }
    }

    private BackupRead<UserEnvBackup> ReadLegacyUserBackup()
    {
        if (!File.Exists(BackupPath))
            return BackupRead<UserEnvBackup>.Missing;

        try
        {
            var raw = JsonSerializer.Deserialize<UserEnvBackup>(ReadBoundedBackupFile(), JsonOptions);
            return ValidateUserBackup(raw, "legacy User file");
        }
        catch (InvalidDataException ex)
        {
            Log.Warning(ex, "Environment: legacy User backup failed validation");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Environment: legacy User backup is corrupt");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Environment: legacy User backup could not be read");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "Environment: legacy User backup read was denied");
            return BackupRead<UserEnvBackup>.Invalid;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Environment: legacy User backup read was denied");
            return BackupRead<UserEnvBackup>.Invalid;
        }
    }

    private static BackupRead<UserEnvBackup> ParseUserBackup(string json, string source)
    {
        if (Encoding.UTF8.GetByteCount(json) is <= 0 or > MaxBackupFileBytes)
        {
            Log.Warning("Environment: {Source} backup has an invalid size", source);
            return BackupRead<UserEnvBackup>.Invalid;
        }

        var raw = JsonSerializer.Deserialize<UserEnvBackup>(json, JsonOptions);
        return ValidateUserBackup(raw, source);
    }

    private static BackupRead<UserEnvBackup> ValidateUserBackup(UserEnvBackup? raw, string source)
    {
        if (raw is null)
            return BackupRead<UserEnvBackup>.Invalid;

        var validated = ValidateScopeBackup(raw.User, raw.UserKinds, source);
        return validated is null
            ? BackupRead<UserEnvBackup>.Invalid
            : BackupRead<UserEnvBackup>.Valid(new UserEnvBackup(validated.Values, validated.Kinds));
    }

    private byte[] ReadBoundedBackupFile()
    {
        using var stream = new FileStream(
            BackupPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        var length = stream.Length;
        if (length is <= 0 or > MaxBackupFileBytes)
            throw new InvalidDataException("The environment backup has an invalid size.");

        var bytes = new byte[(int)length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }

        if (read != bytes.Length || stream.ReadByte() != -1)
            throw new InvalidDataException("The environment backup changed while it was being read.");

        return bytes;
    }

    private BackupRead<MachineEnvBackup> ReadMachineBackup()
    {
        try
        {
            using var key = _machineRoot.OpenSubKey(MachineBackupPath);
            if (key is null)
                return BackupRead<MachineEnvBackup>.Missing;

            if (_enforceMachineBackupProtection && !IsMachineBackupParentProtected())
            {
                Log.Warning("Environment: Machine backup parent key is not access-controlled");
                return BackupRead<MachineEnvBackup>.Invalid;
            }

            if (_enforceMachineBackupProtection &&
                !IsProtectedMachineBackupSecurity(key.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)))
            {
                Log.Warning("Environment: Machine backup key is writable by an untrusted principal");
                return BackupRead<MachineEnvBackup>.Invalid;
            }

            if (!key.GetValueNames().Contains(
                    MachineBackupValueName,
                    StringComparer.OrdinalIgnoreCase))
            {
                return BackupRead<MachineEnvBackup>.Missing;
            }

            var rawValue = key.GetValue(
                MachineBackupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (rawValue is not string json ||
                key.GetValueKind(MachineBackupValueName) != RegistryValueKind.String)
            {
                Log.Warning("Environment: protected Machine backup has an invalid registry type");
                return BackupRead<MachineEnvBackup>.Invalid;
            }
            if (Encoding.UTF8.GetByteCount(json) is <= 0 or > MaxBackupFileBytes)
            {
                Log.Warning("Environment: protected Machine backup has an invalid size");
                return BackupRead<MachineEnvBackup>.Invalid;
            }

            var raw = JsonSerializer.Deserialize<MachineEnvBackup>(json, JsonOptions);
            if (raw is null)
                return BackupRead<MachineEnvBackup>.Invalid;

            var validated = ValidateScopeBackup(
                raw.Machine,
                raw.MachineKinds,
                "protected Machine",
                requireNonEmpty: true);
            return validated is null
                ? BackupRead<MachineEnvBackup>.Invalid
                : BackupRead<MachineEnvBackup>.Valid(
                    new MachineEnvBackup(validated.Values, validated.Kinds));
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Environment: protected Machine backup is corrupt");
            return BackupRead<MachineEnvBackup>.Invalid;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Environment: protected Machine backup could not be read");
            return BackupRead<MachineEnvBackup>.Invalid;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning(ex, "Environment: protected Machine backup read was denied");
            return BackupRead<MachineEnvBackup>.Invalid;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Environment: protected Machine backup read was denied");
            return BackupRead<MachineEnvBackup>.Invalid;
        }
    }

    private static ScopeBackup? ValidateScopeBackup(
        Dictionary<string, string>? values,
        Dictionary<string, RegistryValueKind>? kinds,
        string source,
        bool requireNonEmpty = false)
    {
        if (values is null)
            return RejectScopeBackup(source, "the variables section is missing or null");
        if (requireNonEmpty && values.Count == 0)
            return RejectScopeBackup(source, "the variables section is empty");
        if (values.Count > MaxVariableCount)
            return RejectScopeBackup(source, "the variables section has too many entries");

        Dictionary<string, string> normalizedValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (!TryValidateBackupName(name, out var validatedName))
                return RejectScopeBackup(source, "a variable name is invalid");
            if (value is null)
                return RejectScopeBackup(source, "a variable value is null");
            if (value.Length > MaxVariableValueLength)
                return RejectScopeBackup(source, "a variable value is oversized");
            if (!normalizedValues.TryAdd(validatedName, value))
                return RejectScopeBackup(source, "variable names are duplicated");
        }

        Dictionary<string, RegistryValueKind>? normalizedKinds = null;
        if (kinds is not null)
        {
            if (kinds.Count > MaxVariableCount)
                return RejectScopeBackup(source, "the value-kind section has too many entries");

            normalizedKinds = new Dictionary<string, RegistryValueKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, kind) in kinds)
            {
                if (!TryValidateBackupName(name, out var validatedName))
                    return RejectScopeBackup(source, "a value-kind name is invalid");
                if (!normalizedValues.ContainsKey(validatedName))
                    return RejectScopeBackup(source, "a value kind has no matching variable");
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                    return RejectScopeBackup(source, "a registry value kind is unsupported");
                if (!normalizedKinds.TryAdd(validatedName, kind))
                    return RejectScopeBackup(source, "value-kind names are duplicated");
            }
        }

        return new ScopeBackup(normalizedValues, normalizedKinds);
    }

    private static ScopeBackup? RejectScopeBackup(string source, string reason)
    {
        Log.Warning("Environment: {Source} backup rejected because {Reason}", source, reason);
        return null;
    }

    /// <summary>The outcome of a <see cref="RestoreFromBackup"/> call.</summary>
    public readonly record struct RestoreResult(
        bool HadBackup,
        int Restored,
        int Removed,
        int Failed)
    {
        public bool InvalidBackup { get; init; }
    }

    /// <summary>
    /// Restores each scope only from its authoritative, fully validated snapshot. The
    /// HKCU or legacy LocalAppData data can affect only User variables; Machine variables
    /// are restored only from the access-controlled HKLM snapshot. Every present snapshot
    /// is validated before the first write. Machine writes count as failures when the
    /// process is not elevated. The caller should
    /// <see cref="BroadcastSettingChange"/> once afterwards.
    /// </summary>
    public RestoreResult RestoreFromBackup()
    {
        var userBackup = ReadUserBackup();
        var machineBackup = ReadMachineBackup();
        var hasValidBackup = userBackup.Snapshot is not null || machineBackup.Snapshot is not null;
        if (userBackup.IsInvalid || machineBackup.IsInvalid)
        {
            Log.Warning("Environment: restore aborted because a present backup is invalid");
            return new RestoreResult(hasValidBackup, 0, 0, 0) { InvalidBackup = true };
        }
        if (!hasValidBackup)
            return new RestoreResult(false, 0, 0, 0);

        var counts = default(ScopeRestoreCounts);
        if (userBackup.Snapshot is { } userSnapshot)
            counts = counts.Add(RestoreScope(
                EnvVarScope.User,
                userSnapshot.User,
                userSnapshot.UserKinds));
        if (machineBackup.Snapshot is { } machineSnapshot)
            counts = counts.Add(RestoreScope(
                EnvVarScope.Machine,
                machineSnapshot.Machine,
                machineSnapshot.MachineKinds));

        Log.Information(
            "Environment: restored {Restored}, removed {Removed}, failed {Failed} from backup",
            counts.Restored,
            counts.Removed,
            counts.Failed);
        return new RestoreResult(true, counts.Restored, counts.Removed, counts.Failed);
    }

    private ScopeRestoreCounts RestoreScope(
        EnvVarScope scope,
        Dictionary<string, string> saved,
        Dictionary<string, RegistryValueKind>? kinds)
    {
        int restored = 0, removed = 0, failed = 0;
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

        return new ScopeRestoreCounts(restored, removed, failed);
    }
}
