// SysManager · EnvVariable
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>The registry scope an environment variable lives in.</summary>
public enum EnvVarScope
{
    /// <summary>Per-user variables (HKCU\Environment) — editable without elevation.</summary>
    User,

    /// <summary>System-wide variables (HKLM ...\Session Manager\Environment) — require administrator.</summary>
    Machine
}

/// <summary>
/// A single Windows environment variable. <see cref="Value"/> is observable so the
/// grid can edit it in place; <see cref="Name"/> and <see cref="Scope"/> identify it
/// and never change once loaded.
/// </summary>
public sealed partial class EnvVariable : ObservableObject
{
    [ObservableProperty] private string _value = "";

    public required string Name { get; init; }
    public required EnvVarScope Scope { get; init; }

    /// <summary>
    /// True when the variable is stored as REG_EXPAND_SZ (its value may contain %VAR%
    /// tokens that Windows expands). Preserved on write so editing a PATH never flips it
    /// to a plain REG_SZ and freezes its tokens. Defaults to false for new variables; the
    /// service promotes a new value containing '%' to expandable automatically.
    /// </summary>
    public bool IsExpandable { get; init; }

    /// <summary>
    /// True for variables whose value is a ';'-separated list worth showing in the list
    /// editor — PATH, PATHEXT, and any *PATH variable. Note PATHEXT is a list of file
    /// EXTENSIONS, not directories, so use <see cref="IsDirectoryList"/> before treating
    /// entries as paths (e.g. checking directory existence).
    /// </summary>
    public bool IsPathLike =>
        Name.Equals("Path", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
        Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for list variables whose entries are directories (so directory-existence
    /// checks are meaningful) — every <see cref="IsPathLike"/> variable EXCEPT PATHEXT,
    /// whose entries are file extensions (.COM;.EXE;.BAT;…). Without this distinction the
    /// PATH editor ran <c>Directory.Exists(".COM")</c> on each PATHEXT entry and flagged
    /// them all as "missing", showing the whole list in red.
    /// </summary>
    public bool IsDirectoryList =>
        IsPathLike && !Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable scope label for the grid ("User" / "System").</summary>
    public string ScopeLabel => Scope == EnvVarScope.Machine ? "System" : "User";
}
