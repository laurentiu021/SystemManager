// SysManager · TracerouteService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
    public int TimeoutMs { get; set; } = 2000;
    public int ProbesPerHop { get; set; } = 2;

    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct)
    {
        List<TracerouteHop> results = [];
        var payload = new byte[32];

        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            var options = new PingOptions(ttl, true);
            List<double> latencies = [];
            IPAddress? replyAddress = null;
            // Track the destination-reached status across ALL probes of this hop, not
            // just the last one: if probe 1 reaches the destination (Success) but a
            // later probe times out, the last-probe status would otherwise mislabel
            // the hop and fail to stop the traceroute at the destination.
            bool reachedDestination = false;
            IPStatus replyStatus = IPStatus.Unknown;

            for (int probe = 0; probe < ProbesPerHop; probe++)
            {
                try
                {
                    using var ping = new Ping();
                    var sw = Stopwatch.StartNew();
                    var effectiveTimeout = TimeoutMs > 0 ? TimeoutMs : 3000;
                    var reply = await ping.SendPingAsync(host, effectiveTimeout, payload, options).WaitAsync(ct).ConfigureAwait(false);
                    sw.Stop();

                    if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                    {
                        replyAddress ??= reply.Address;
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                        // Prefer Success (destination) over TtlExpired (intermediate hop)
                        // when summarizing the hop's status.
                        if (replyStatus != IPStatus.Success) replyStatus = reply.Status;
                        if (reply.Status == IPStatus.Success) reachedDestination = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (System.Net.NetworkInformation.PingException) { /* per-probe failure; reported as timeout */ }
                catch (System.Net.Sockets.SocketException) { /* per-probe failure; reported as timeout */ }
                catch (InvalidOperationException) { /* per-probe failure; reported as timeout */ }
            }

            var hop = new TracerouteHop
            {
                HopNumber = ttl,
                Address = replyAddress?.ToString() ?? "*",
                LatencyMs = latencies.Count > 0 ? latencies.Average() : null,
                Status = latencies.Count > 0 ? replyStatus.ToString() : "Timeout"
            };

            // Await reverse DNS with a short timeout before emitting the hop.
            // This ensures hop.HostName is populated when the UI receives it.
            if (replyAddress is not null)
            {
                var addr = replyAddress;
                try
                {
                    using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    dnsCts.CancelAfter(800); // 800ms max for reverse DNS
                    // Pass the token INTO GetHostEntryAsync rather than wrapping it in
                    // WaitAsync: WaitAsync only abandons the await, leaving the lookup
                    // running in the background (an orphaned task). The cancellation-aware
                    // overload actually cancels the lookup when the 800ms budget elapses.
                    var entry = await Dns.GetHostEntryAsync(addr.ToString(), dnsCts.Token).ConfigureAwait(false);
                    hop.HostName = entry.HostName;
                }
                catch (OperationCanceledException) { /* DNS too slow — leave as null */ }
                catch (System.Net.Sockets.SocketException) { /* no reverse DNS record */ }
            }

            results.Add(hop);
            RaiseHopCompleted(hop);

            if (reachedDestination) break; // any probe reached the destination
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
        if (handlers is null) return;
        foreach (var h in handlers)
        {
            try { ((Action<TracerouteHop>)h).Invoke(hop); }
            catch (ObjectDisposedException) { /* subscriber disposed — skip */ }
            catch (InvalidOperationException) { /* subscriber error — skip */ }
        }
    }
}
