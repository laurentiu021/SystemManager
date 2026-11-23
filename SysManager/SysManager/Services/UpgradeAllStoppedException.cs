// SysManager · UpgradeAllStoppedException
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// <see cref="IWingetService.UpgradeAllAsync"/> stopped before its next app because it was asked to. The app being
/// upgraded when it was asked finished first, so it is not left half-installed.
/// </summary>
/// <param name="reached">How many of the apps it ran, the one that was running included.</param>
/// <param name="listed">How many apps it was going to upgrade.</param>
/// <param name="token">The token that asked it to stop.</param>
public sealed class UpgradeAllStoppedException(int reached, int listed, CancellationToken token)
    : OperationCanceledException("Update All Apps stopped before its next app.", token)
{
    /// <summary>How many of the apps it ran, the one that was running when it was asked to stop included.</summary>
    public int Reached { get; } = reached;

    /// <summary>How many apps it was going to upgrade.</summary>
    public int Listed { get; } = listed;
}
