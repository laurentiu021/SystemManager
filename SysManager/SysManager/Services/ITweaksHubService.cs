// SysManager · ITweaksHubService — testable seam for the Tweaks Hub
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="TweaksHubService"/> so the ViewModel can be unit-tested with a
/// substituted implementation (no real registry writes / restore point). Mirrors the
/// established interface-seam pattern (<see cref="IAppBlockerService"/>, <see cref="IPowerShellRunner"/>).
/// </summary>
public interface ITweaksHubService
{
    IReadOnlyList<TweakItem> LoadTweaks();

    /// <summary>
    /// Applies (enable=true) or reverts (enable=false) the given tweaks. Returns the result:
    /// the items that failed to write, and whether a System Restore point was actually created
    /// before the first change (so the UI can report honestly rather than over-promise).
    /// </summary>
    Task<TweakApplyResult> ApplyAsync(IReadOnlyList<TweakItem> tweaks, bool enable, CancellationToken ct = default);
}

/// <summary>Outcome of a Tweaks Hub apply/undo batch.</summary>
public sealed record TweakApplyResult(IReadOnlyList<TweakItem> Failed, bool RestorePointCreated);
