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

    /// <summary>List running processes with their current affinity mask (0 if unreadable) and start time.</summary>
    IReadOnlyList<RunningProcess> GetProcesses();

    /// <summary>
    /// Read the current affinity mask for a process, or null if unavailable. Null too when the process with that ID
    /// did not start at <paramref name="startTime"/>.
    /// </summary>
    /// <param name="processId">The process ID.</param>
    /// <param name="startTime">
    /// When the process started, as listed, or null when Windows would not say, which skips the check. Windows gives a
    /// closed process's ID to the next one started, so an ID from a list can name a different program (#2514).
    /// </param>
    long? GetAffinity(int processId, DateTime? startTime);

    /// <summary>
    /// Apply an affinity mask to a process. Returns true on success; on failure sets
    /// <paramref name="error"/>. A mask of 0 is rejected (Windows treats it as
    /// "OS decides", which is not what an explicit selection means). A process with that ID that did not start at
    /// <paramref name="startTime"/> is left alone and reported as no longer running.
    /// </summary>
    /// <param name="processId">The process ID.</param>
    /// <param name="startTime">When the process started, as listed, or null when Windows would not say.</param>
    /// <param name="mask">The logical CPUs to allow, one bit each.</param>
    /// <param name="error">Why nothing was changed, in words for the status line.</param>
    bool TrySetAffinity(int processId, DateTime? startTime, long mask, out string error);

    /// <summary>
    /// True when the process listed with <paramref name="processId"/> and <paramref name="startTime"/> is no longer
    /// running: nothing has that ID, or the ID now belongs to a process that started at another time. False while it
    /// runs, and when Windows will not say when the process with that ID started.
    /// </summary>
    bool HasExited(int processId, DateTime? startTime);

    /// <summary>
    /// Read the current scheduling priority class for a process, or null if unavailable
    /// (exited / access denied). Used by Gaming Profile to capture the original priority
    /// before raising it, so revert can restore the exact prior value. Null too when the process with that ID did
    /// not start at <paramref name="startTime"/>.
    /// </summary>
    /// <param name="processId">The process ID.</param>
    /// <param name="startTime">When the process started, as listed, or null when Windows would not say.</param>
    ProcessPriorityClass? GetPriority(int processId, DateTime? startTime);

    /// <summary>
    /// Set a process's scheduling priority class. Returns true on success; on failure sets
    /// <paramref name="error"/>. Your own processes need no admin; another user's / an
    /// elevated process raises access-denied, surfaced cleanly (mirrors
    /// <see cref="TrySetAffinity"/>). A process with that ID that did not start at <paramref name="startTime"/> is
    /// left alone and reported as no longer running.
    /// </summary>
    /// <param name="processId">The process ID.</param>
    /// <param name="startTime">When the process started, as listed, or null when Windows would not say.</param>
    /// <param name="priority">The priority class to set.</param>
    /// <param name="error">Why nothing was changed, in words for the status line.</param>
    bool TrySetPriority(int processId, DateTime? startTime, ProcessPriorityClass priority, out string error);
}
