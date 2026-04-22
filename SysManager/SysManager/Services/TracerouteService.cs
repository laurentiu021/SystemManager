// SysManager · TracerouteService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Performs a traceroute by sending ICMP echo requests with incrementing TTL
/// and inspecting the TtlExpired responses. Does not require admin rights and
/// returns structured data the UI can chart directly.
/// </summary>
public sealed class TracerouteService
{
    public event Action<TracerouteHop>? HopCompleted;

    public int MaxHops { get; set; } = 30;
    public int TimeoutMs { get; set; } = 3000;
    public int ProbesPerHop { get; set; } = 2;

    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct)
    {
        var results = new List<TracerouteHop>();
        var payload = new byte[32];

        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            var options = new PingOptions(ttl, true);
            var latencies = new List<double>();
            IPAddress? replyAddress = null;
            IPStatus lastStatus = IPStatus.Unknown;

            for (int probe = 0; probe < ProbesPerHop; probe++)
            {
                try
                {
                    using var ping = new Ping();
                    var sw = Stopwatch.StartNew();
                    var effectiveTimeout = TimeoutMs > 0 ? TimeoutMs : 3000;
                    var reply = await ping.SendPingAsync(host, effectiveTimeout, payload, options).WaitAsync(ct);
                    sw.Stop();
                    lastStatus = reply.Status;

                    if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                    {
                        replyAddress ??= reply.Address;
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* swallow per-probe errors; we report them as a timeout */ }
            }

            var hop = new TracerouteHop
            {
                HopNumber = ttl,
                Address = replyAddress?.ToString() ?? "*",
                LatencyMs = latencies.Count > 0 ? latencies.Average() : null,
                Status = latencies.Count > 0 ? lastStatus.ToString() : "Timeout"
            };

            // Best-effort reverse DNS, non-blocking and respecting cancellation.
            if (replyAddress != null)
            {
                var addr = replyAddress;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = await Dns.GetHostEntryAsync(addr);
                        hop.HostName = entry.HostName;
                    }
                    catch { /* ignore: reverse DNS is advisory */ }
                }, ct);
            }

            results.Add(hop);
            RaiseHopCompleted(hop);

            if (lastStatus == IPStatus.Success) break; // reached destination
        }

        return results;
    }

    /// <summary>
    /// Invokes HopCompleted subscribers with isolation — a faulty handler
    /// must never abort the traceroute nor block other subscribers.
    /// </summary>
    private void RaiseHopCompleted(TracerouteHop hop)
    {
        var handlers = HopCompleted?.GetInvocationList();
        if (handlers == null) return;
        foreach (var h in handlers)
        {
            try { ((Action<TracerouteHop>)h).Invoke(hop); }
            catch { /* swallow subscriber errors */ }
        }
    }
}
