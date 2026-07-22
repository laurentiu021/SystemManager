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

    private sealed class PidCounters
    {
        public long DownBytes;
        public long UpBytes;
        public long PrevDownBytes;
        public long PrevUpBytes;
        public string Name = "";
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

    private void Add(int pid, int down, int up, string name)
    {
        if (pid <= 0) return;
        var c = _counters.GetOrAdd(pid, _ => new PidCounters());
        if (down > 0) System.Threading.Interlocked.Add(ref c.DownBytes, down);
        if (up > 0) System.Threading.Interlocked.Add(ref c.UpBytes, up);
        if (c.Name.Length == 0 && !string.IsNullOrEmpty(name)) c.Name = name;
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

        return Task.FromResult(new BandwidthSnapshot(BandwidthMode.PreciseEtw, totalDown, totalUp, ordered));
    }

    private static long NowTicks() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond;

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
