// SysManager · TracerouteMonitorService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs a traceroute against every enabled target on a schedule, reporting
/// completed routes via <see cref="RouteCompleted"/>. Slower and heavier than
/// the ping monitor, so interval defaults to 60s.
///
/// One traceroute per target runs sequentially within a cycle to avoid
/// flooding the local network with simultaneous TTL scans.
/// </summary>
public sealed class TracerouteMonitorService : IDisposable
{
    private readonly TracerouteService _tracer = new()
    {
        MaxHops = 30,
        TimeoutMs = 2000,
        ProbesPerHop = 1
    };

    public event Action<string, IReadOnlyList<TracerouteHop>>? RouteCompleted;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    public ConcurrentDictionary<string, PingTarget> Targets { get; } = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Lock _stateLock = new();

    public bool IsRunning => _loop is { IsCompleted: false };

    public void AddOrUpdate(PingTarget target) => Targets[target.Host] = target;
    public void Remove(string host) => Targets.TryRemove(host, out _);

    /// <summary>
    /// Convenience method: ensures a host is tracked without requiring a
    /// full <see cref="PingTarget"/> instance. If the host is already
    /// tracked, this is a no-op.
    /// </summary>
    public void AddHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        Targets.GetOrAdd(host, h => new PingTarget { Host = h, Name = h, IsEnabled = true });
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => PumpAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            var cts = _cts;
            var loop = _loop;
            _cts = null;
            _loop = null;
            if (cts is null) return;

            cts.Cancel();
            try { loop?.Wait(3000); }
            catch (AggregateException) { /* task cancellation or faulted — expected during stop */ }
            catch (ObjectDisposedException) { /* task already cleaned up */ }

            // Dispose the CTS only once the loop has actually finished. A traceroute cycle is
            // heavy (a full route per target), so the 3 s Wait can time out while PumpAsync is
            // still using the token; disposing now would throw ObjectDisposedException on the
            // background thread. Defer disposal to a continuation in that case so the CTS is
            // neither used-after-dispose nor leaked. (Mirrors PingMonitorService.Stop.)
            if (loop is null || loop.IsCompleted)
                cts.Dispose();
            else
                loop.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Kick off the first cycle immediately, then wait Interval between cycles.
        while (!ct.IsCancellationRequested)
        {
            var active = Targets.Values
                .Where(t => t.IsEnabled && !string.IsNullOrWhiteSpace(t.Host))
                .ToArray();

            foreach (var target in active)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var hops = await _tracer.RunAsync(target.Host, ct).ConfigureAwait(false);
                    // Skip reporting if the target was removed/disabled mid-flight.
                    if (Targets.TryGetValue(target.Host, out var live) && live.IsEnabled)
                        RouteCompleted?.Invoke(target.Host, hops);
                }
                catch (OperationCanceledException) { return; }
                catch (System.Net.Sockets.SocketException) { /* network error — skip */ }
                catch (System.Net.NetworkInformation.PingException) { /* ping failed — skip */ }
                catch (TimeoutException) { /* target unreachable — skip */ }
                catch (InvalidOperationException) { /* traceroute failed for this target — skip */ }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
