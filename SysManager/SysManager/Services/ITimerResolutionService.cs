// SysManager · ITimerResolutionService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="TimerResolutionService"/> — controls the Windows
/// multimedia timer resolution. Extracting this interface lets
/// <c>TimerResolutionViewModel</c>'s mutating command paths (Enable / Disable) be
/// unit-tested with a substituted service instead of issuing real
/// <c>NtSetTimerResolution</c> calls against the host process (Gate-ARCH:
/// system-mutating services are testable).
/// </summary>
public interface ITimerResolutionService
{
    /// <summary>Read the achievable range and the resolution currently in effect.</summary>
    TimerResolutionStatus Query();

    /// <summary>
    /// Request the finest achievable timer resolution (clamped to the device's
    /// reported minimum). Returns the status after re-querying the effective value.
    /// </summary>
    TimerResolutionStatus Enable();

    /// <summary>
    /// Release this process's timer-resolution request, returning the timer toward the
    /// system default. Returns the status after re-querying the effective value.
    /// </summary>
    TimerResolutionStatus Disable();
}
