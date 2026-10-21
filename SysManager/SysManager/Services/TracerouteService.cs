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

        // Resolve the destination ONCE, up front, and probe that fixed IP for every TTL.
        // Passing the raw hostname into SendPingAsync inside the loop re-resolves DNS on
        // every probe (up to MaxHops*ProbesPerHop lookups) and — for a round-robin / CDN
        // hostname — lets consecutive TTLs target DIFFERENT IPs, producing a garbled route
        // that interleaves hops toward different servers.
        var destination = await ResolveDestinationAsync(host, ct).ConfigureAwait(false);

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
                    var reply = await ping.SendPingAsync(destination, effectiveTimeout, payload, options).WaitAsync(ct).ConfigureAwait(false);
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

            // No reverse lookup of the hop's address. It held each hop back for up to 800 ms, more where the lookup
            // could not be cancelled, and auto-trace repeated it for every target on every round, for a host name
            // that nothing in the app showed since the console line that printed it was removed (#611).
            results.Add(hop);
            RaiseHopCompleted(hop);

            if (reachedDestination) break; // any probe reached the destination
        }

        return results;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to a single fixed <see cref="IPAddress"/> to probe
    /// for the whole trace. An IP literal is used as-is (no DNS); otherwise the first
    /// address returned by DNS is pinned. A resolution failure is surfaced as
    /// <see cref="InvalidOperationException"/> — the type the ViewModel already handles —
    /// so an unresolvable host shows a friendly "Error" message instead of crashing the
    /// command with a raw <see cref="System.Net.Sockets.SocketException"/>.
    /// </summary>
    /// <remarks>
    /// "0", "0.0.0.0" and "::" parse as the address meaning "any", which names no host, and Ping refuses it with an
    /// <see cref="ArgumentException"/>, as DNS does a name of more than 255 characters. Nothing caught that type, so a
    /// trace of such a host raised the error dialog, and in auto-trace ended the loop for every target (#2870). Both are
    /// refused here in the type the callers show.
    /// </remarks>
    internal static async Task<IPAddress> ResolveDestinationAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.Equals(IPAddress.Any) || literal.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException($"'{host}' is not the address of a host, so it cannot be traced.");
            return literal;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
                throw new InvalidOperationException($"Could not resolve host '{host}'.");
            return addresses[0];
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new InvalidOperationException($"Could not resolve host '{host}': {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Could not resolve host '{host}': {ex.Message}", ex);
        }
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
