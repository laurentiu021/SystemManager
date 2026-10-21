// SysManager · IFeatureParentsService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads which Windows optional features another one needs: the parents that turning it on with <c>-All</c> turns on
/// too. Behind an interface so the Windows Features tab is tested without asking the DISM API.
/// </summary>
public interface IFeatureParentsService
{
    /// <summary>
    /// Every feature <paramref name="featureName"/> needs: its parents, theirs and so on, nearest first and each once,
    /// with whether each is on. Null when DISM could not be asked.
    /// </summary>
    Task<IReadOnlyList<FeatureParent>?> ReadAsync(string featureName, CancellationToken ct = default);
}
