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

    /// <summary>True for PATH-like variables whose value is a ';'-separated directory list.</summary>
    public bool IsPathLike =>
        Name.Equals("Path", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
        Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable scope label for the grid ("User" / "System").</summary>
    public string ScopeLabel => Scope == EnvVarScope.Machine ? "System" : "User";
}
