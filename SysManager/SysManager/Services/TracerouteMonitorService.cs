// SysManager · TracerouteMonitorService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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

    public bool IsRunning => _loop is { IsCompleted: false };

    public void AddOrUpdate(PingTarget target) => Targets[target.Host] = target;
    public void Remove(string host) => Targets.TryRemove(host, out _);

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PumpAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
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
                    var hops = await _tracer.RunAsync(target.Host, ct);
                    // Skip reporting if the target was removed/disabled mid-flight.
                    if (Targets.TryGetValue(target.Host, out var live) && live.IsEnabled)
                        RouteCompleted?.Invoke(target.Host, hops);
                }
                catch (OperationCanceledException) { return; }
                catch { /* swallow per-target errors */ }
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
