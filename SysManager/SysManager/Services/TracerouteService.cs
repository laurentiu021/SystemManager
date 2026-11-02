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
    /// <summary>
    /// The echo payload, 32 bytes as Windows' tracert sends. Ping only reads it, so one copy serves every probe.
    /// </summary>
    private static readonly byte[] Payload = new byte[32];

    /// <summary>The answer to one probe: what came back, and the address it came from.</summary>
    internal readonly record struct ProbeReply(IPStatus Status, IPAddress? Address);

    private readonly Func<IPAddress, int, int, CancellationToken, Task<ProbeReply>> _probe;

    public event Action<TracerouteHop>? HopCompleted;

    public int MaxHops { get; set; } = 30;
    public int TimeoutMs { get; set; } = 2000;
    public int ProbesPerHop { get; set; } = 2;

    public TracerouteService() : this(SendProbeAsync) { }

    /// <summary>The service, with the probe given by a test.</summary>
    /// <param name="probe">Sends one probe: the destination, the TTL and the timeout in milliseconds.</param>
    internal TracerouteService(Func<IPAddress, int, int, CancellationToken, Task<ProbeReply>> probe) => _probe = probe;

    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct)
    {
        List<TracerouteHop> results = [];

        // Resolve the destination ONCE, up front, and probe that fixed IP for every TTL.
        // Passing the raw hostname into SendPingAsync inside the loop re-resolves DNS on
        // every probe (up to MaxHops*ProbesPerHop lookups) and — for a round-robin / CDN
        // hostname — lets consecutive TTLs target DIFFERENT IPs, producing a garbled route
        // that interleaves hops toward different servers.
        var destination = await ResolveDestinationAsync(host, ct).ConfigureAwait(false);

        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            List<double> latencies = [];
            IPAddress? replyAddress = null;
            // Track the destination-reached status across ALL probes of this hop, not
            // just the last one: if probe 1 reaches the destination (Success) but a
            // later probe times out, the last-probe status would otherwise mislabel
            // the hop and fail to stop the traceroute at the destination.
            bool reachedDestination = false;
            IPStatus replyStatus = IPStatus.Unknown;
            // A router that answers that it cannot reach the destination, or that refuses it, is where the path ends.
            // Its answer names it and says why, so it is the last hop, not a timeout before 30 more (#2751).
            IPStatus? refusal = null;
            IPAddress? refusedBy = null;

            for (int probe = 0; probe < ProbesPerHop; probe++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var effectiveTimeout = TimeoutMs > 0 ? TimeoutMs : 3000;
                    var reply = await _probe(destination, ttl, effectiveTimeout, ct).ConfigureAwait(false);
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
                    else if (IsRefusal(reply.Status))
                    {
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                        refusal ??= reply.Status;
                        refusedBy ??= reply.Address;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (System.Net.NetworkInformation.PingException) { /* per-probe failure; reported as timeout */ }
                catch (System.Net.Sockets.SocketException) { /* per-probe failure; reported as timeout */ }
                catch (InvalidOperationException) { /* per-probe failure; reported as timeout */ }
            }

            // Reaching the destination outranks a refusal from another probe of the same hop.
            var refused = !reachedDestination && refusal is not null;
            var hop = new TracerouteHop
            {
                HopNumber = ttl,
                Address = (refused ? refusedBy : replyAddress)?.ToString() ?? "*",
                LatencyMs = latencies.Count > 0 ? latencies.Average() : null,
                Status = refused ? refusal!.Value.ToString() : latencies.Count > 0 ? replyStatus.ToString() : "Timeout"
            };

            // No reverse lookup of the hop's address. It held each hop back for up to 800 ms, more where the lookup
            // could not be cancelled, and auto-trace repeated it for every target on every round, for a host name
            // that nothing in the app showed since the console line that printed it was removed (#611).
            results.Add(hop);
            RaiseHopCompleted(hop);

            if (reachedDestination || refused) break; // the destination answered, or a router said it cannot be reached
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
    /// The answers that end the path at the router that sent them: no route to the destination's network or to the
    /// destination, or a refusal. Protocol unreachable has the value of prohibited, so it is one of them too.
    /// </summary>
    private static bool IsRefusal(IPStatus status) => status is IPStatus.DestinationNetworkUnreachable
        or IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable
        or IPStatus.DestinationProhibited or IPStatus.DestinationPortUnreachable;

    /// <summary>
    /// What the router that ended a trace answered, in words; null for a hop that did not end it that way.
    /// </summary>
    internal static string? DescribeRefusal(TracerouteHop hop) =>
        !Enum.TryParse<IPStatus>(hop.Status, out var status) ? null : status switch
        {
            IPStatus.DestinationNetworkUnreachable => "it has no route to the destination's network",
            IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable => "it cannot reach the destination",
            IPStatus.DestinationProhibited or IPStatus.DestinationPortUnreachable
                => "the destination refused the trace",
            _ => null,
        };

    /// <summary>
    /// One ICMP echo with the TTL given, answered by the destination or by the router where the TTL ran out.
    /// </summary>
    private static async Task<ProbeReply> SendProbeAsync(
        IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(destination, timeoutMs, Payload, new PingOptions(ttl, true))
            .WaitAsync(ct).ConfigureAwait(false);
        return new ProbeReply(reply.Status, reply.Address);
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
