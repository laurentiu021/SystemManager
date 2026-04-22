// SysManager · PingMonitorService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Continuously pings a set of targets in parallel at a fixed interval and
/// raises a SampleReceived event for every reply (including timeouts/errors).
/// Uses System.Net.NetworkInformation.Ping — no external process spawning,
/// no console output pollution, and no admin required.
///
/// The service is a long-lived singleton: Start() spins up one pumping task
/// that loops at Interval and fires off one ping per enabled target per tick.
/// Callers mutate the Targets collection freely; changes take effect on the
/// next tick.
/// </summary>
public sealed class PingMonitorService : IDisposable
{
    public event Action<PingSample>? SampleReceived;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public int TimeoutMs { get; set; } = 2000;

    // Targets are referenced by host so enabling/disabling from the UI is cheap.
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
        try { _loop?.Wait(1500); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Floor the interval so a user-provided zero / negative value doesn't
        // turn the pump into a CPU-bound busy loop.
        static TimeSpan Clamp(TimeSpan t) =>
            t < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : t;

        while (!ct.IsCancellationRequested)
        {
            // Snapshot enabled targets for this tick.
            var active = Targets.Values.Where(t => t.IsEnabled && !string.IsNullOrWhiteSpace(t.Host)).ToArray();

            // Fire-and-forget each ping so the pump cadence is driven by Interval,
            // not by the slowest timeout. Exceptions are reported as samples.
            foreach (var target in active)
                _ = PingOnceAsync(target, ct);

            try { await Task.Delay(Clamp(Interval), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PingOnceAsync(PingTarget target, CancellationToken ct)
    {
        var host = target.Host;
        double? latency = null;
        var status = "OK";
        try
        {
            using var ping = new Ping();
            // A timeout of 0 or negative is illegal for Ping; coerce into a usable floor.
            var effectiveTimeout = TimeoutMs > 0 ? TimeoutMs : 2000;
            var reply = await ping.SendPingAsync(host, effectiveTimeout).WaitAsync(ct);
            if (reply.Status == IPStatus.Success)
            {
                latency = reply.RoundtripTime;
            }
            else
            {
                status = reply.Status.ToString();
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            status = ex.GetType().Name;
        }

        // Drop stale samples: the target may have been removed or disabled
        // while the ping was in flight. Prevents UI binding to ghost data.
        if (!Targets.TryGetValue(host, out var current) || !current.IsEnabled) return;
        if (ct.IsCancellationRequested) return;

        var sample = new PingSample(DateTime.UtcNow, host, latency, status);
        RaiseSampleReceived(sample);
    }

    /// <summary>
    /// Invokes subscribers one at a time, isolating each from the others.
    /// A faulty subscriber must never poison the pump or block sibling handlers.
    /// </summary>
    private void RaiseSampleReceived(PingSample sample)
    {
        var handlers = SampleReceived?.GetInvocationList();
        if (handlers == null) return;
        foreach (var h in handlers)
        {
            try { ((Action<PingSample>)h).Invoke(sample); }
            catch { /* swallow subscriber errors */ }
        }
    }

    public void Dispose() => Stop();
}
