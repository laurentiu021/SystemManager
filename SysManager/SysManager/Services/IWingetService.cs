// SysManager · IWingetService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="WingetService"/> so ViewModels depend on a mockable seam
/// (Gate-ARCH) instead of shelling winget directly. Lets an upgrade path be unit-tested
/// against a substituted service — no real winget process is spawned in tests — and keeps
/// the whole app on ONE winget invocation instead of hand-rolled command lines that drift.
/// </summary>
public interface IWingetService
{
    /// <summary>Live output lines streamed from the underlying winget process.</summary>
    event Action<PowerShellLine>? LineReceived;

    /// <summary>Runs 'winget upgrade' and returns the upgradable packages.</summary>
    Task<List<AppPackage>> ListUpgradableAsync(CancellationToken ct = default);

    /// <summary>Upgrades a single package by its winget id.</summary>
    Task<WingetResult> UpgradeAsync(string packageId, CancellationToken ct = default);

    /// <summary>Upgrades every package that has an update available.</summary>
    Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default);
}
