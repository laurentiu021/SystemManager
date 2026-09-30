// SysManager · CpuAffinityService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads CPU topology (P-core / E-core classification on Intel hybrid CPUs) and gets/
/// sets per-process CPU affinity. Setting affinity uses .NET's
/// <see cref="Process.ProcessorAffinity"/>; it applies only to the running process and
/// is lost when that process exits, so it's inherently temporary and reversible (the
/// caller captures the original mask to restore). Affinity for your own processes needs
/// no admin; other users' / elevated processes raise access-denied, surfaced cleanly.
///
/// Scope: a single processor group (≤64 logical processors), which covers all consumer
/// hybrid CPUs. Topology detection uses the kernel32 GetLogicalProcessorInformationEx
/// API via classic <c>[DllImport]</c> (the returned buffer is a variable-length record
/// stream walked by each record's Size — not something the source generator marshals).
/// </summary>
public sealed class CpuAffinityService : ICpuAffinityService
{
    /// <summary>Total logical processors as Windows schedules them.</summary>
    public int LogicalProcessorCount => Environment.ProcessorCount;

    /// <summary>
    /// Enumerate each logical CPU with its hybrid classification. Returns a plain
    /// 0..N-1 "Standard" list if the topology API is unavailable or fails.
    /// </summary>
    public IReadOnlyList<CpuCore> GetCores()
    {
        try
        {
            var cores = ReadTopology();
            if (cores.Count > 0) return cores;
        }
        catch (Win32Exception ex) { Log.Debug("CPU topology query failed: {Error}", ex.Message); }
        catch (DllNotFoundException ex) { Log.Debug("CPU topology API unavailable: {Error}", ex.Message); }

        // Fallback: flat list, all Standard.
        var list = new List<CpuCore>(LogicalProcessorCount);
        for (int i = 0; i < LogicalProcessorCount; i++)
            list.Add(new CpuCore(i, 0, "Standard"));
        return list;
    }

    /// <summary>List running processes with their current affinity mask (0 if unreadable) and start time.</summary>
    public IReadOnlyList<RunningProcess> GetProcesses()
    {
        var result = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long mask;
                try { mask = (long)p.ProcessorAffinity; }
                catch (Win32Exception) { mask = 0; }
                result.Add(new RunningProcess(p.Id, p.ProcessName, mask, StartTimeOf(p)));
            }
            catch (InvalidOperationException) { /* exited between enumerate and read */ }
            finally { p.Dispose(); }
        }
        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>When <paramref name="p"/> started, or null when Windows will not say.</summary>
    private static DateTime? StartTimeOf(Process p)
    {
        try { return p.StartTime; }
        catch (Win32Exception) { return null; }
    }

    /// <summary>
    /// Read the current affinity mask for a process, or null if unavailable. Null too when the process with that ID
    /// did not start at <paramref name="startTime"/>.
    /// </summary>
    public long? GetAffinity(int processId, DateTime? startTime)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            if (!IsListed(p, startTime)) return null;
            p.Refresh();
            return (long)p.ProcessorAffinity;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    /// <summary>
    /// Apply an affinity mask to a process. Returns true on success; on failure sets
    /// <paramref name="error"/>. A mask of 0 is rejected (Windows treats it as
    /// "OS decides", which is not what an explicit selection means). A process with that ID that did not start at
    /// <paramref name="startTime"/> is left alone and reported as no longer running.
    /// </summary>
    public bool TrySetAffinity(int processId, DateTime? startTime, long mask, out string error)
    {
        error = "";
        if (mask == 0)
        {
            error = "Select at least one CPU core.";
            return false;
        }
        long allCores = AllCoresMask(LogicalProcessorCount);
        if ((mask & ~allCores) != 0)
        {
            error = "The selection includes a CPU that does not exist.";
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(processId);
            if (!IsListed(p, startTime))
            {
                error = "That process is no longer running.";
                return false;
            }
            p.ProcessorAffinity = (IntPtr)mask;
            return true;
        }
        catch (ArgumentException)
        {
            error = "That process is no longer running.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "That process has exited.";
            return false;
        }
        catch (Win32Exception ex)
        {
            Log.Debug("Set affinity for {Pid} failed: {Error}", processId, ex.Message);
            error = "Couldn't change this process — it may belong to another user and need administrator rights.";
            return false;
        }
    }

    /// <summary>
    /// True when the process listed with <paramref name="processId"/> and <paramref name="startTime"/> is no longer
    /// running: nothing has that ID, or the ID now belongs to a process that started at another time. False while it
    /// runs, and when Windows will not say when the process with that ID started.
    /// </summary>
    /// <remarks>
    /// A process that has exited while another program still holds a handle to it keeps its ID and start time until
    /// that handle closes. <see cref="Process.GetProcessById(int)"/> refuses it all the same, as it refuses an ID
    /// nothing has, so no separate exit check is needed; the integration suite pins that on a child it holds.
    /// </remarks>
    public bool HasExited(int processId, DateTime? startTime)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return !IsListed(p, startTime);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>
    /// Whether <paramref name="p"/> is the process listed as starting at <paramref name="startTime"/>. Always so when
    /// the list had no start time to give.
    /// </summary>
    /// <remarks>
    /// The check <see cref="ProcessManagerService.KillProcess"/> makes before ending a process, for the same reason:
    /// the list can be minutes old, and Windows gives a closed process's ID to the next one started (#2514). Reading
    /// the start time throws <see cref="Win32Exception"/> when Windows will not say, and
    /// <see cref="InvalidOperationException"/> when the process has exited; each caller handles both.
    /// </remarks>
    private static bool IsListed(Process p, DateTime? startTime) =>
        startTime is not { } listed || p.StartTime == listed;

    /// <summary>Read the current scheduling priority class for a process, or null if unavailable.</summary>
    public ProcessPriorityClass? GetPriority(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Refresh();
            return p.PriorityClass;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    /// <summary>
    /// Set a process's scheduling priority class. Returns true on success; on failure sets
    /// <paramref name="error"/> (mirrors <see cref="TrySetAffinity"/>'s error idiom).
    /// </summary>
    public bool TrySetPriority(int processId, ProcessPriorityClass priority, out string error)
    {
        error = "";
        try
        {
            using var p = Process.GetProcessById(processId);
            p.PriorityClass = priority;
            return true;
        }
        catch (ArgumentException)
        {
            error = "That process is no longer running.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "That process has exited.";
            return false;
        }
        catch (Win32Exception ex)
        {
            Log.Debug("Set priority for {Pid} failed: {Error}", processId, ex.Message);
            error = "Couldn't change this process — it may belong to another user and need administrator rights.";
            return false;
        }
    }

    /// <summary>Bitmask with the low <paramref name="count"/> bits set (all logical CPUs).</summary>
    public static long AllCoresMask(int count) => count >= 64 ? -1L : (1L << count) - 1;

    /// <summary>Build a mask from a set of logical-CPU indices.</summary>
    public static long MaskFromIndices(IEnumerable<int> indices)
    {
        long mask = 0;
        foreach (int i in indices)
            if (i is >= 0 and < 64) mask |= 1L << i;
        return mask;
    }

    /// <summary>True if logical CPU <paramref name="index"/> is set in <paramref name="mask"/>.</summary>
    public static bool IsCoreInMask(long mask, int index) => index is >= 0 and < 64 && (mask & (1L << index)) != 0;

    // ── Topology via GetLogicalProcessorInformationEx ──────────────────
    private static List<CpuCore> ReadTopology()
    {
        uint length = 0;
        // Pass 1: probe required size (must fail with ERROR_INSUFFICIENT_BUFFER).
        if (NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, IntPtr.Zero, ref length))
            return [];
        if (Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer || length == 0)
            return [];

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, buffer, ref length))
                return [];

            var raw = new List<(int Index, byte Efficiency)>();
            byte maxEff = 0, minEff = byte.MaxValue;

            long offset = 0;
            while (offset < length)
            {
                IntPtr record = buffer + (int)offset;
                int size = Marshal.ReadInt32(record, NativeMethods.OffsetSize);
                if (size <= 0 || offset + size > length) break; // guard against corrupt record

                byte efficiency = Marshal.ReadByte(record, NativeMethods.OffsetEfficiencyClass);
                long groupMask = IntPtr.Size == 8
                    ? Marshal.ReadInt64(record, NativeMethods.OffsetGroupMask0)
                    : Marshal.ReadInt32(record, NativeMethods.OffsetGroupMask0);

                for (int bit = 0; bit < 64; bit++)
                {
                    if ((groupMask & (1L << bit)) != 0)
                    {
                        raw.Add((bit, efficiency));
                        if (efficiency > maxEff) maxEff = efficiency;
                        if (efficiency < minEff) minEff = efficiency;
                    }
                }
                offset += size;
            }

            bool hybrid = maxEff != minEff;
            var cores = new List<CpuCore>(raw.Count);
            foreach (var (index, efficiency) in raw)
            {
                string label = !hybrid ? "Standard"
                    : efficiency == maxEff ? "Performance" : "Efficiency";
                cores.Add(new CpuCore(index, efficiency, label));
            }
            cores.Sort((a, b) => a.LogicalIndex.CompareTo(b.LogicalIndex));
            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class NativeMethods
    {
        public const int RelationProcessorCore = 0;
        public const int ErrorInsufficientBuffer = 122;

        // Fixed byte offsets inside each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        // (RelationProcessorCore variant) on x64.
        public const int OffsetSize = 4;            // DWORD Size at +4
        public const int OffsetEfficiencyClass = 9; // +8 Flags, +9 EfficiencyClass
        public const int OffsetGroupMask0 = 32;     // GROUP_AFFINITY GroupMask[0].Mask

        // No A/W variant (not a string function) → no EntryPoint suffix needed.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
    }
}
