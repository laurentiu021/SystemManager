// SysManager · EnvironmentVariableService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
/// Writes go through <see cref="Environment.SetEnvironmentVariable(string,string,EnvironmentVariableTarget)"/>,
/// which updates the registry AND broadcasts WM_SETTINGCHANGE so already-running
/// processes (Explorer, new shells) pick up the change without a reboot.
///
/// Machine-scope writes require administrator rights; <see cref="SetVariable"/> returns
/// <c>false</c> (rather than throwing) when the write is denied, mirroring
/// <see cref="PrivacyService"/>. A timestamped JSON backup of every variable is written
/// before the first mutation so the user can fully restore the original environment.
/// </summary>
public sealed partial class EnvironmentVariableService
{
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
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.()\-]*$")]
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

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Reads all variables for the given scope, sorted by name.</summary>
    public List<EnvVariable> Read(EnvVarScope scope)
    {
        List<EnvVariable> result = [];
        var target = scope == EnvVarScope.Machine
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;
        try
        {
            var vars = Environment.GetEnvironmentVariables(target);
            foreach (System.Collections.DictionaryEntry kv in vars)
            {
                var name = kv.Key?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new EnvVariable
                {
                    Name = name,
                    Scope = scope,
                    Value = kv.Value?.ToString() ?? ""
                });
            }
        }
        catch (System.Security.SecurityException ex)
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
    /// Sets (or, when <paramref name="value"/> is null, deletes) a variable. Validates
    /// the name at the trust boundary. Returns <c>false</c> if the write is denied
    /// (typically a Machine-scope write without elevation) instead of throwing.
    /// </summary>
    public bool SetVariable(string name, string? value, EnvVarScope scope)
    {
        var validName = ValidateName(name);
        var target = scope == EnvVarScope.Machine
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;
        try
        {
            Environment.SetEnvironmentVariable(validName, value, target);
            Log.Information("Environment: {Action} {Scope} variable {Name}",
                value is null ? "deleted" : "set", scope, validName);
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

    /// <summary>Deletes a variable. Returns false if the write is denied.</summary>
    public bool DeleteVariable(string name, EnvVarScope scope) => SetVariable(name, null, scope);

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
        var snapshot = new EnvBackup(
            User: ToDict(Read(EnvVarScope.User)),
            Machine: ToDict(Read(EnvVarScope.Machine)));
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

    /// <summary>A point-in-time snapshot of both environment scopes.</summary>
    public sealed record EnvBackup(
        Dictionary<string, string> User,
        Dictionary<string, string> Machine);

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
}
