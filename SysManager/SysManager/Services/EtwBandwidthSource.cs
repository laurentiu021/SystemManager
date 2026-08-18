// SysManager · EtwBandwidthSource — precise per-process byte rates via a kernel ETW session (admin)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The elevated, precise bandwidth source. It opens a kernel ETW session subscribed to the
/// TCP/IP + UDP/IP network keyword and accumulates per-process send/receive byte counts from the
/// <c>TcpIpSend</c>/<c>TcpIpRecv</c>/<c>UdpIpSend</c>/<c>UdpIpRecv</c> events (plus their IPv6
/// variants). Each <see cref="SampleAsync"/> converts the bytes accumulated since the previous
/// call into per-process download/upload rates — the same figures Task Manager's Network column
/// shows.
/// <para>
/// A kernel session needs administrator, so this is only constructed when the app is already
/// elevated and the user opts in. It is defensive by construction: if the session can't start
/// (missing privilege, a stale same-named session, a locked-down host, or the native
/// KernelTraceControl helper failing to load), <see cref="Start"/> returns false and
/// <see cref="IsAvailable"/> stays false so the ViewModel silently falls back to the no-admin
/// <see cref="ConnectionBandwidthSource"/> — the tab never crashes because ETW was unavailable.
/// </para>
/// <para>Strictly local and read-only: the trace stays in-process and nothing is written or sent.</para>
/// </summary>
public sealed class EtwBandwidthSource : IBandwidthMonitorService
{
    // A fixed, unique session name so a leftover session from a crashed run can be found and
    // stopped rather than colliding. Not user-supplied.
    private const string SessionName = "SysManagerBandwidthKernel";

    public BandwidthMode Mode => BandwidthMode.PreciseEtw;
    public bool IsAvailable { get; private set; }

    private TraceEventSession? _session;
    private Task? _processingTask;
    private bool _disposed;
    private long _prevTimestampTicks;

    // PID -> cumulative bytes since the session started. Concurrent because the ETW callbacks
    // fire on the session's processing thread while SampleAsync reads on the UI/poll thread.
    private readonly ConcurrentDictionary<int, PidCounters> _counters = new();

    /// <summary>
    /// How long a PID may sit with no new bytes before it is dropped from <see cref="_counters"/>.
    /// <para>Without this the dictionary only ever grew — it was cleared in <see cref="Dispose"/> and
    /// nowhere else — so every PID that ever resolved a DNS name stayed in the per-tick sort for the
    /// tab's lifetime: installers, updaters, every browser child process. An all-day elevated session
    /// accumulated thousands, and each second re-allocated and re-sorted all of them. Ten minutes keeps
    /// a genuinely idle-but-running app (a mail client polling on a long interval) visible with its
    /// session totals, while a process that has been gone for a workday does not cost anything. Windows
    /// recycles PIDs, so a stale entry is not merely useless — a new process inheriting the number would
    /// inherit its totals.</para>
    /// </summary>
    private static readonly TimeSpan IdleEviction = TimeSpan.FromMinutes(10);

    private sealed class PidCounters
    {
        public long DownBytes;
        public long UpBytes;
        public long PrevDownBytes;
        public long PrevUpBytes;
        public string Name = "";

        /// <summary>Tick count when bytes last arrived — the eviction clock. Written by ETW callbacks.</summary>
        public long LastActivityTicks;
    }

    public bool Start()
    {
        if (_session is not null) return IsAvailable;
        // Elevation is required for a kernel session; check up front so a non-elevated caller
        // gets a clean false instead of an access-denied deep inside TraceEvent.
        if (!Helpers.AdminHelper.IsElevated())
        {
            Log.Debug("Bandwidth ETW: not elevated — precise mode unavailable");
            return false;
        }

        try
        {
            // Stop a stale session left by a previous crashed run (kernel sessions outlive the
            // process). GetActiveSession + Stop is the documented recovery path.
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            { Log.Debug("Bandwidth ETW: could not stop stale session: {Error}", ex.Message); }

            _session = new TraceEventSession(SessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = _session.Source.Kernel;
            kernel.TcpIpRecv += d => Add(d.ProcessID, down: d.size, up: 0, d.ProcessName);
            kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, down: d.size, up: 0, d.ProcessName);
            kernel.TcpIpSend += d => Add(d.ProcessID, down: 0, up: d.size, d.ProcessName);
            kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, down: 0, up: d.size, d.ProcessName);
            kernel.UdpIpRecv += d => Add(d.ProcessID, down: d.size, up: 0, d.ProcessName);
            kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, down: d.size, up: 0, d.ProcessName);
            kernel.UdpIpSend += d => Add(d.ProcessID, down: 0, up: d.size, d.ProcessName);
            kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, down: 0, up: d.size, d.ProcessName);

            // Process the trace on a background thread; Source.Process() blocks until the session
            // is stopped/disposed. Guard so a mid-stream fault disables the source (the poll then
            // sees IsAvailable=false and the VM falls back) rather than crashing the app.
            _processingTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex)
                {
                    Log.Debug("Bandwidth ETW: processing ended: {Error}", ex.Message);
                    IsAvailable = false;
                }
            });

            _prevTimestampTicks = NowTicks();
            IsAvailable = true;
            Log.Information("Bandwidth ETW: kernel session started");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                     or System.ComponentModel.Win32Exception or TypeInitializationException
                                     or DllNotFoundException or System.IO.FileNotFoundException)
        {
            // Any failure to start the kernel session → unavailable, and the VM falls back to the
            // no-admin source. TypeInitialization/DllNotFound cover the native KernelTraceControl
            // helper failing to load (e.g. blocked from the single-file extraction dir).
            Log.Warning("Bandwidth ETW: could not start kernel session: {Error}", ex.Message);
            SafeStop();
            IsAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Accumulate one event's bytes against a PID. <c>internal</c> rather than private so the eviction
    /// contract can be tested without a kernel ETW session (which needs administrator): the tests drive
    /// this and <see cref="SampleAsync"/> directly with a fake clock. Same seam idea as
    /// <c>EtaCalculator</c>'s injected <see cref="TimeProvider"/>.
    /// </summary>
    internal void Add(int pid, int down, int up, string name)
    {
        if (pid <= 0) return;

        // Stamp inside the factory, not after GetOrAdd returns. GetOrAdd PUBLISHES the entry the instant it
        // is created, so stamping afterwards leaves a window where _counters holds an entry whose
        // LastActivityTicks is still 0 — and against a monotonic clock 0 is far below the cutoff, so a poll
        // landing in that window evicts a PID that is actively transferring. Its next event re-adds it with
        // a zeroed counter, which the user sees as the session total for a busy app resetting to a few KB.
        var c = _counters.GetOrAdd(pid, _ => new PidCounters { LastActivityTicks = NowTicks() });
        if (down > 0) System.Threading.Interlocked.Add(ref c.DownBytes, down);
        if (up > 0) System.Threading.Interlocked.Add(ref c.UpBytes, up);
        if (c.Name.Length == 0 && !string.IsNullOrEmpty(name)) c.Name = name;
        // Stamped on every event, so eviction measures real inactivity rather than age.
        System.Threading.Volatile.Write(ref c.LastActivityTicks, NowTicks());
    }

    public Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default)
    {
        long nowTicks = NowTicks();
        double elapsed = Math.Max(0.001, (nowTicks - _prevTimestampTicks) / (double)TimeSpan.TicksPerSecond);
        _prevTimestampTicks = nowTicks;

        double totalDown = 0, totalUp = 0;
        var rows = new List<ProcessNetworkUsage>();
        foreach (var (pid, c) in _counters)
        {
            long down = System.Threading.Interlocked.Read(ref c.DownBytes);
            long up = System.Threading.Interlocked.Read(ref c.UpBytes);
            double downRate = BandwidthFormat.RatePerSecond(c.PrevDownBytes, down, elapsed);
            double upRate = BandwidthFormat.RatePerSecond(c.PrevUpBytes, up, elapsed);
            c.PrevDownBytes = down;
            c.PrevUpBytes = up;

            totalDown += downRate;
            totalUp += upRate;

            // Only surface processes that have transferred something this session, so idle PIDs
            // don't clutter the list. A zero-rate row for an app that WAS active still shows its
            // running totals, which is useful, so keep any PID with non-zero cumulative bytes.
            if (down == 0 && up == 0) continue;
            rows.Add(new ProcessNetworkUsage
            {
                ProcessId = pid,
                ProcessName = c.Name.Length > 0 ? c.Name : $"PID {pid}",
                DownBytesPerSec = downRate,
                UpBytesPerSec = upRate,
                TotalDownBytes = down,
                TotalUpBytes = up,
                ConnectionCount = 0, // not tracked in ETW mode; the rate columns carry the signal
            });
        }

        var ordered = rows
            .OrderByDescending(r => r.DownBytesPerSec + r.UpBytesPerSec)
            .ThenByDescending(r => r.TotalDownBytes + r.TotalUpBytes)
            .ToList();

        EvictIdlePids(nowTicks);

        return Task.FromResult(new BandwidthSnapshot(BandwidthMode.PreciseEtw, totalDown, totalUp, ordered));
    }

    /// <summary>
    /// Drops PIDs that have transferred nothing for <see cref="IdleEviction"/>, bounding the per-tick
    /// cost by what is CURRENTLY active rather than by everything the session has ever seen.
    /// </summary>
    /// <remarks>
    /// Runs after the snapshot is built, so a PID evicted on this tick still appears one last time — the
    /// list does not lose a row the user was looking at mid-glance. <c>TryRemove</c> on a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> races safely against the ETW callback thread: if
    /// bytes arrive for an evicted PID the very next event re-adds it via <c>GetOrAdd</c>, and the only
    /// cost is that its session totals restart — acceptable for a process that has been silent for ten
    /// minutes, and unavoidable anyway, since Windows recycles PIDs.
    /// </remarks>
    private void EvictIdlePids(long nowTicks)
    {
        long cutoff = nowTicks - IdleEviction.Ticks;
        foreach (var (pid, c) in _counters)
        {
            // Entries are only ever created by Add, which stamps them in the GetOrAdd factory, so an entry
            // is never visible here unstamped. (Stamping after GetOrAdd returned was not enough: the entry
            // is published on creation, so a poll could see a 0 and evict a PID mid-transfer.)
            // This deliberately does NOT treat 0 as "unstamped": zero is a legitimate reading —
            // a monotonic clock starts there, both in a test that has not advanced it and on a real
            // machine in the first tick after the source starts — and skipping it made a PID stamped at
            // 0 immortal. Not hypothetical: it turned two eviction tests red the moment the clock became
            // monotonic, which is exactly what a sentinel value inside the real value range does.
            if (System.Threading.Volatile.Read(ref c.LastActivityTicks) < cutoff)
                _counters.TryRemove(pid, out _);
        }
    }

    /// <summary>
    /// The eviction/rate clock. Injectable so the eviction contract is testable without waiting ten real
    /// minutes — a test passes a stub and advances it. Production passes nothing and gets
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates the source without opening an ETW session — that happens in <see cref="Start"/>. Constructing
    /// this is therefore free and needs no elevation, which is what lets the view model hold one and only
    /// pay for the trace if the user turns per-process mode on while running elevated.
    /// </summary>
    /// <param name="timeProvider">Test seam; defaults to the system clock.</param>
    public EtwBandwidthSource(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// A MONOTONIC tick count, in <see cref="TimeSpan"/> ticks. Both callers subtract two readings, so this
    /// clock must only ever move forward.
    /// </summary>
    /// <remarks>
    /// <para>This was <c>Environment.TickCount64</c>, which is monotonic. Adding the injectable seam
    /// briefly made it <c>GetUtcNow().UtcTicks</c> — the WALL clock — and that breaks both callers, because
    /// a wall clock steps backwards on an NTP correction or when the user sets the time.</para>
    /// <para>In <c>SampleAsync</c> the byte delta is divided by the elapsed seconds. A backwards step makes
    /// that difference negative, <c>Math.Max(0.001, …)</c> clamps it to a millisecond, and every rate on
    /// that tick is multiplied by roughly a thousand — an absurd spike in the one place this tab exists to
    /// report accurately. <c>BandwidthFormat.RatePerSecond</c> guards a non-positive elapsed and a negative
    /// delta, but not a tiny elapsed, so nothing downstream catches it.</para>
    /// <para>In <c>EvictIdlePids</c> each PID's stamp is compared against <c>now</c> minus ten minutes. A
    /// forward step larger than the window makes every stamp look stale at once, dropping the whole table
    /// and every session total with it.</para>
    /// <para><see cref="TimeProvider.GetTimestamp"/> is the monotonic reading, and
    /// <see cref="TimeProvider.GetElapsedTime(long, long)"/> converts a pair using the provider's own
    /// <c>TimestampFrequency</c> — so this stays in TimeSpan ticks whatever the platform's raw frequency is,
    /// and a stub only has to override those two members, which is what the existing fake in
    /// <c>EtaCalculatorTests</c> already does.</para>
    /// </remarks>
    private long NowTicks() => _time.GetElapsedTime(0, _time.GetTimestamp()).Ticks;

    private void SafeStop()
    {
        try { _session?.Stop(); } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { Log.Debug("Bandwidth ETW: stop error: {Error}", ex.Message); }
        try { _session?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Log.Debug("Bandwidth ETW: dispose error: {Error}", ex.Message); }
        _session = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsAvailable = false;
        // Stopping the session unblocks Source.Process() so the processing task completes.
        SafeStop();
        try { _processingTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* processing faulted/cancelled during teardown — fine */ }
        _counters.Clear();
    }
}
