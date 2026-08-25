// SysManager · RunningProcess
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A running process the user can target for CPU affinity. <see cref="AffinityMask"/>
/// is the current processor-affinity bitmask (bit i = logical CPU i), or 0 if it
/// couldn't be read (e.g. access denied).
/// </summary>
public sealed record RunningProcess(int ProcessId, string Name, long AffinityMask)
{
    public string Display => $"{Name} ({ProcessId})";

    /// <summary>
    /// Like <see cref="Display"/>, but appends how many cores the process is pinned to when its
    /// affinity is a real subset — e.g. <c>"chrome (1234) — 4 of 16 cores"</c>. The tab already reads
    /// the mask and then discarded it; this surfaces it so the user can see at a glance which
    /// processes are already tuned, instead of selecting each one to infer it from the checkboxes.
    /// <para>Bound by the picker, not by Gaming Profile (which uses <c>DisplayMemberPath="Name"</c>),
    /// so it is added alongside <see cref="Display"/> rather than replacing it.</para>
    /// </summary>
    public string PinnedDisplay => DescribeAffinity(Name, ProcessId, AffinityMask, Environment.ProcessorCount);

    /// <summary>
    /// The picker label, with a neutral "N of M cores" suffix ONLY when the mask is a genuine subset of
    /// the machine's cores. A mask of 0 (unreadable) or all-cores (not pinned) gets no suffix. Wording is
    /// deliberately neutral — it reports the observed state, it does not claim SysManager set it, since a
    /// job object or hypervisor could have. Pure and static so the branches are unit-testable without a
    /// real process or a fixed core count.
    /// </summary>
    internal static string DescribeAffinity(string name, int processId, long mask, int logicalCount)
    {
        var label = $"{name} ({processId})";
        if (logicalCount <= 0) return label;

        long allCores = logicalCount >= 64 ? -1L : (1L << logicalCount) - 1;
        // 0 = mask unreadable (access denied); all-cores = the default, not pinned. Neither is worth a
        // suffix — only a real subset is. Masked with allCores so a stray high bit can't defeat the check.
        if (mask == 0 || (mask & allCores) == allCores) return label;

        int pinned = System.Numerics.BitOperations.PopCount((ulong)(mask & allCores));
        return $"{label} — {pinned} of {logicalCount} cores";
    }
}
