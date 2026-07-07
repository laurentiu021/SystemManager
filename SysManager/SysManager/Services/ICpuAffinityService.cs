// SysManager · ICpuAffinityService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="CpuAffinityService"/> — reads CPU topology and gets/sets
/// per-process CPU affinity. Extracting this interface lets <c>CpuAffinityViewModel</c>'s
/// mutating command paths (Apply / Restore) be unit-tested with a substituted service
/// against a deterministic process+topology instead of touching a real process
/// (Gate-ARCH: system-mutating services are testable).
///
/// <para>Only the instance members are abstracted; the pure bitmask helpers
/// (<c>AllCoresMask</c> / <c>MaskFromIndices</c> / <c>IsCoreInMask</c>) remain static
/// on <see cref="CpuAffinityService"/>.</para>
/// </summary>
public interface ICpuAffinityService
{
    /// <summary>Total logical processors as Windows schedules them.</summary>
    int LogicalProcessorCount { get; }

    /// <summary>
    /// Enumerate each logical CPU with its hybrid classification. Returns a plain
    /// 0..N-1 "Standard" list if the topology API is unavailable or fails.
    /// </summary>
    IReadOnlyList<CpuCore> GetCores();

    /// <summary>List running processes with their current affinity mask (0 if unreadable).</summary>
    IReadOnlyList<RunningProcess> GetProcesses();

    /// <summary>Read the current affinity mask for a process, or null if unavailable.</summary>
    long? GetAffinity(int processId);

    /// <summary>
    /// Apply an affinity mask to a process. Returns true on success; on failure sets
    /// <paramref name="error"/>. A mask of 0 is rejected (Windows treats it as
    /// "OS decides", which is not what an explicit selection means).
    /// </summary>
    bool TrySetAffinity(int processId, long mask, out string error);

    /// <summary>
    /// Read the current scheduling priority class for a process, or null if unavailable
    /// (exited / access denied). Used by Gaming Profile to capture the original priority
    /// before raising it, so revert can restore the exact prior value.
    /// </summary>
    ProcessPriorityClass? GetPriority(int processId);

    /// <summary>
    /// Set a process's scheduling priority class. Returns true on success; on failure sets
    /// <paramref name="error"/>. Your own processes need no admin; another user's / an
    /// elevated process raises access-denied, surfaced cleanly (mirrors
    /// <see cref="TrySetAffinity"/>).
    /// </summary>
    bool TrySetPriority(int processId, ProcessPriorityClass priority, out string error);
}
