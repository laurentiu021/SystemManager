// SysManager · StoreApp
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A removable Windows Store (Appx/MSIX) app, as reported by <c>Get-AppxPackage</c>.
/// <see cref="IsSelected"/> drives bulk removal; <see cref="IsProtected"/> marks
/// system-critical packages the denylist refuses to remove (shown disabled in the UI).
/// </summary>
public sealed partial class StoreApp : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    /// <summary>The PackageFullName — the exact identifier passed to Remove-AppxPackage.</summary>
    public required string PackageFullName { get; init; }

    /// <summary>The PackageFamilyName (stable across versions) — used for matching/denylist.</summary>
    public required string PackageFamilyName { get; init; }

    /// <summary>The bare package name (e.g. "Microsoft.WindowsCalculator").</summary>
    public required string Name { get; init; }

    /// <summary>A friendly display name when the curated catalog knows one, else <see cref="Name"/>.</summary>
    public required string DisplayName { get; init; }

    public required string Publisher { get; init; }
    public required string Version { get; init; }

    /// <summary>One-line description of what the app is and what removing it affects.</summary>
    public string Description { get; init; } = "";

    /// <summary>True for system-critical packages that must never be removed (denylisted).</summary>
    public bool IsProtected { get; init; }

    /// <summary>True for items in the curated "commonly removed bloat" preset.</summary>
    public bool IsCommonBloat { get; init; }
}
