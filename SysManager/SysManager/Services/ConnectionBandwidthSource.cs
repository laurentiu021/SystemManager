// SysManager · ConnectionBandwidthSource — no-admin total throughput + per-process connections
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The default, no-administrator bandwidth source. It combines two OS signals:
/// <list type="bullet">
/// <item><b>Accurate machine-wide throughput</b> — summed byte counters across all operational,
/// non-loopback network interfaces (<see cref="NetworkInterface.GetIPStatistics"/>), turned into
/// per-second rates by delta between polls.</item>
/// <item><b>Per-process attribution by active connection</b> — the extended TCP/UDP tables
/// (<c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c>, iphlpapi) map every open socket to its
/// owning PID, so the tab can show WHICH apps are talking, to which remote ports, and how many
/// connections each holds.</item>
/// </list>
/// This deliberately does NOT report exact per-process byte rates — Windows only exposes those
/// through an elevated ETW session (see <c>EtwBandwidthSource</c>). Everything here works for a
/// standard user with no elevation and no extra dependency, and is strictly read-only.
/// <para>
/// The per-connection enumeration is separated into an overridable virtual so the aggregation
/// logic can be unit-tested without a live network stack.
/// </para>
/// </summary>
public class ConnectionBandwidthSource : IBandwidthMonitorService
{
    public BandwidthMode Mode => BandwidthMode.Connections;
    public bool IsAvailable { get; private set; }

    private long _prevBytesReceived;
    private long _prevBytesSent;
    private long _prevTimestampTicks;
    private bool _primed;

    public bool Start()
    {
        // Prime the counters so the first SampleAsync reports a real delta rather than a spike
        // from process start. Always available — no admin, no external dependency.
        (_prevBytesReceived, _prevBytesSent) = ReadInterfaceTotals();
        _prevTimestampTicks = NowTicks();
        _primed = true;
        IsAvailable = true;
        return true;
    }

    /// <summary>
    /// Takes one snapshot. Synchronous by design — the caller owns the offload.
    /// </summary>
    /// <remarks>
    /// <para>Getting this off the UI thread is the point, and it now happens ONCE, in
    /// <c>BandwidthMonitorViewModel.PollOnceAsync</c>. The work here is not bounded by a small constant:
    /// enumerating all network interfaces and reading per-interface IP statistics, two native TCP/UDP
    /// table queries, and a <c>Process.GetProcessById</c> for each PID not yet in the name cache. A
    /// machine with many adapters or a browser holding dozens of sockets pays for all of it, so running
    /// it inline made the window stutter continuously for as long as the tab was open (1.61.9).</para>
    /// <para>This method used to wrap itself in <c>Task.Run</c>. That fixed this source and left its
    /// ETW sibling — which returned <c>Task.FromResult</c> — running inline, an asymmetry no test
    /// enforced and the interface did not document. The offload moved to the consumer so both modes are
    /// covered by construction; wrapping again here would just add a second hop.</para>
    /// <para>Everything the poll touches is confined to this call and its own fields, which only this
    /// timer-driven path writes, so running it on a worker thread is safe. The returned snapshot is
    /// immutable; the ViewModel marshals back to the UI thread when it assigns properties.</para>
    /// </remarks>
    public Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default)
    {
        if (!_primed) Start();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SampleCore());
    }

    /// <summary>
    /// The actual sample. Separated from <see cref="SampleAsync"/> so the work is one plain synchronous
    /// unit that the offload wraps, rather than being interleaved with scheduling concerns.
    /// </summary>
    private BandwidthSnapshot SampleCore()
    {
        var (rx, tx) = ReadInterfaceTotals();
        long nowTicks = NowTicks();
        double elapsed = Math.Max(0, (nowTicks - _prevTimestampTicks) / (double)TimeSpan.TicksPerSecond);

        double down = BandwidthFormat.RatePerSecond(_prevBytesReceived, rx, elapsed);
        double up = BandwidthFormat.RatePerSecond(_prevBytesSent, tx, elapsed);

        _prevBytesReceived = rx;
        _prevBytesSent = tx;
        _prevTimestampTicks = nowTicks;

        var processes = AggregateConnections(EnumerateConnections());
        return new BandwidthSnapshot(BandwidthMode.Connections, down, up, processes);
    }

    /// <summary>
    /// Folds a flat connection list into one <see cref="ProcessNetworkUsage"/> row per PID, with a
    /// connection count and a short remote-port summary. Pure (no OS access) so it is unit-tested
    /// directly; the icon is left null here and attached by the ViewModel on the UI thread.
    /// </summary>
    internal static IReadOnlyList<ProcessNetworkUsage> AggregateConnections(IEnumerable<ConnectionRow> rows)
    {
        var byPid = new Dictionary<int, (string Name, List<int> Ports)>();
        foreach (var row in rows)
        {
            if (row.ProcessId <= 0) continue; // 0 = System Idle / unattributable
            if (!byPid.TryGetValue(row.ProcessId, out var agg))
            {
                agg = (row.ProcessName, new List<int>());
                byPid[row.ProcessId] = agg;
            }
            agg.Ports.Add(row.RemotePort);
        }

        return byPid
            .Select(kv => new ProcessNetworkUsage
            {
                ProcessId = kv.Key,
                ProcessName = string.IsNullOrWhiteSpace(kv.Value.Name) ? $"PID {kv.Key}" : kv.Value.Name,
                ConnectionCount = kv.Value.Ports.Count,
                RemoteSummary = BandwidthFormat.SummarizePorts(kv.Value.Ports),
            })
            .OrderByDescending(p => p.ConnectionCount)
            .ThenBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A single active socket mapped to its owning process. Protected-internal so tests
    /// (via InternalsVisibleTo) and subclasses that override <see cref="EnumerateConnections"/> can
    /// build rows, while matching the accessibility of the protected enumeration seam.</summary>
    protected internal readonly record struct ConnectionRow(int ProcessId, string ProcessName, int RemotePort, bool IsTcp);

    /// <summary>
    /// Enumerates current TCP and UDP connections mapped to owning PIDs. Virtual so tests can
    /// substitute a fixed set without the native tables. Never throws — a P/Invoke failure logs
    /// and yields an empty set (the tab then shows total throughput with no per-app rows).
    /// </summary>
    protected virtual IReadOnlyList<ConnectionRow> EnumerateConnections()
    {
        var rows = new List<ConnectionRow>();
        try
        {
            // One name cache per enumeration, owned here — see ResolveName for why it is not static.
            // Shared across the TCP and UDP passes so a PID with both is resolved once, not twice.
            var nameCache = new Dictionary<int, string>();
            NativeTables.CollectTcp(rows, nameCache);
            NativeTables.CollectUdp(rows, nameCache);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            Log.Debug("Bandwidth: connection enumeration failed: {Error}", ex.Message);
        }
        return rows;
    }

    /// <summary>Sums received/sent byte counters across all up, non-loopback interfaces.</summary>
    private static (long Rx, long Tx) ReadInterfaceTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch (NetworkInformationException ex) { Log.Debug("Bandwidth: interface totals read failed: {Error}", ex.Message); }
        return (rx, tx);
    }

    // Monotonic clock for the rate window. Environment.TickCount64 (not DateTime.Now) so a system
    // clock change or DST shift can't produce a negative/huge elapsed and thus a bogus rate.
    private static long NowTicks() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond;

    // Nothing unmanaged to release — the interface totals and native tables are read per-poll and
    // freed immediately. Present to satisfy IBandwidthMonitorService : IDisposable.
    public void Dispose() { }

    // ── Native TCP/UDP table enumeration (iphlpapi) ─────────────────────────
    // Classic [DllImport] with a NativeMethods class: GetExtendedTcpTable returns a
    // variable-length table into a caller-sized buffer (the same idiom CpuAffinityService
    // documents for GetLogicalProcessorInformationEx). Read-only OS query, no admin.
    private static class NativeTables
    {
        private const int AfInet = 2;                       // IPv4
        private const int ErrorInsufficientBuffer = 122;
        // TCP_TABLE_OWNER_PID_ALL / UDP_TABLE_OWNER_PID: rows carry the owning PID.
        private const int TcpTableOwnerPidAll = 5;
        private const int UdpTableOwnerPid = 1;

        internal static void CollectTcp(
            List<ConnectionBandwidthSource.ConnectionRow> rows, Dictionary<int, string> nameCache)
        {
            foreach (var (pid, remotePort) in QueryTable(isTcp: true))
                rows.Add(new ConnectionBandwidthSource.ConnectionRow(pid, ResolveName(pid, nameCache), remotePort, IsTcp: true));
        }

        internal static void CollectUdp(
            List<ConnectionBandwidthSource.ConnectionRow> rows, Dictionary<int, string> nameCache)
        {
            foreach (var (pid, remotePort) in QueryTable(isTcp: false))
                rows.Add(new ConnectionBandwidthSource.ConnectionRow(pid, ResolveName(pid, nameCache), remotePort, IsTcp: false));
        }

        /// <summary>
        /// PID→name resolution against a cache the CALLER owns, so repeated PIDs (a browser holding
        /// twenty sockets) cost one <c>Process.GetProcessById</c>. Names only; no MainModule/path read.
        /// </summary>
        /// <remarks>
        /// The cache used to be a private STATIC Dictionary that <c>QueryTable</c> cleared on entry. Two
        /// problems, both fixed by making it a parameter. It was shared process-wide across every source
        /// instance while a plain Dictionary is not thread-safe — harmless while the poll ran on the UI
        /// thread, but the sample now runs on a worker, and a mode switch constructing a new source could
        /// mutate it concurrently. And the clear-on-entry meant the UDP pass wiped the names the TCP pass
        /// had just resolved, so a PID with both kinds of socket was looked up twice per poll for nothing.
        /// One cache per sample: correct lifetime, no sharing, no clearing.
        /// </remarks>
        private static string ResolveName(int pid, Dictionary<int, string> nameCache)
        {
            if (nameCache.TryGetValue(pid, out var cached)) return cached;
            string name;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); name = p.ProcessName + ".exe"; }
            catch (ArgumentException) { name = $"PID {pid}"; }   // process exited between table read and lookup
            catch (InvalidOperationException) { name = $"PID {pid}"; }
            nameCache[pid] = name;
            return name;
        }

        private static IEnumerable<(int Pid, int RemotePort)> QueryTable(bool isTcp)
        {
            int size = 0;
            int tableClass = isTcp ? TcpTableOwnerPidAll : UdpTableOwnerPid;

            // First call sizes the buffer.
            _ = isTcp
                ? NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, tableClass, 0)
                : NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, tableClass, 0);
            if (size <= 0) yield break;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int result = isTcp
                    ? NativeMethods.GetExtendedTcpTable(buffer, ref size, false, AfInet, tableClass, 0)
                    : NativeMethods.GetExtendedUdpTable(buffer, ref size, false, AfInet, tableClass, 0);
                if (result == ErrorInsufficientBuffer) yield break; // table grew between calls; skip this poll
                if (result != 0) yield break;

                int count = Marshal.ReadInt32(buffer);              // dwNumEntries (first DWORD)
                // Rows begin after the leading DWORD; each row's layout differs by protocol.
                // TCP row (MIB_TCPROW_OWNER_PID): state(4) local(4) localport(4) remote(4) remoteport(4) pid(4) = 24
                // UDP row (MIB_UDPROW_OWNER_PID): local(4) localport(4) pid(4) = 12  (no remote endpoint)
                int rowSize = isTcp ? 24 : 12;
                IntPtr rowPtr = buffer + 4;
                for (int i = 0; i < count; i++)
                {
                    if (isTcp)
                    {
                        int remotePortRaw = Marshal.ReadInt32(rowPtr, 16); // remote port, network byte order
                        int pid = Marshal.ReadInt32(rowPtr, 20);
                        yield return (pid, NetworkToHostPort(remotePortRaw));
                    }
                    else
                    {
                        int pid = Marshal.ReadInt32(rowPtr, 8);
                        yield return (pid, 0); // UDP has no remote port in this table
                    }
                    rowPtr += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // The extended tables store ports in network byte order in the low 16 bits.
        private static int NetworkToHostPort(int raw)
        {
            int lo = raw & 0xFF;
            int hi = (raw >> 8) & 0xFF;
            return (lo << 8) | hi;
        }

        private static class NativeMethods
        {
            // No A/W variant (not a string function) → no EntryPoint suffix needed.
            [DllImport("iphlpapi.dll", SetLastError = true)]
            public static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
                [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);

            [DllImport("iphlpapi.dll", SetLastError = true)]
            public static extern int GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
                [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
        }
    }
}
