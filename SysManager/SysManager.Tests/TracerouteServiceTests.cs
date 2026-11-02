// SysManager · TracerouteServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.NetworkInformation;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="TracerouteService"/>'s destination-resolution seam —
/// the IP-literal fast path is deterministic and offline (no DNS, no system deps).
/// Live DNS/tracing is covered in the integration test project.
/// </summary>
public class TracerouteServiceTests
{
    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("8.8.8.8")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public async Task ResolveDestinationAsync_IpLiteral_ReturnsSameAddress_NoDns(string literal)
    {
        // Regression (#1404): the destination is resolved ONCE up front and every
        // probe targets that fixed IP, instead of re-resolving the hostname per probe
        // (which, for a round-robin/CDN host, could point consecutive TTLs at different
        // servers). An IP literal must be used verbatim with no DNS lookup — so this
        // runs offline and deterministically.
        var resolved = await TracerouteService.ResolveDestinationAsync(literal, CancellationToken.None);
        Assert.Equal(IPAddress.Parse(literal), resolved);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task ResolveDestinationAsync_TheAnyAddress_IsRefusedInTheTypeTheTabShows(string host)
    {
        // It names no host, and Ping refused it with an ArgumentException nothing caught: the error dialog on Trace now,
        // and the end of the loop for every target in auto-trace (#2870). Offline: the literal never reaches DNS.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TracerouteService.ResolveDestinationAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveDestinationAsync_ANameDnsRefuses_IsRefusedInTheTypeTheTabShows()
    {
        // More than 255 characters: DNS refuses it with an ArgumentOutOfRangeException before it asks anything.
        var host = new string('a', 300) + ".example";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TracerouteService.ResolveDestinationAsync(host, CancellationToken.None));
        Assert.IsAssignableFrom<ArgumentException>(ex.InnerException);
    }

    // ---------- a router's answer that the destination cannot be reached (#2751) ----------
    // Only Success and TtlExpired were kept, so a router that answered "no route" or refused showed as "*", a
    // timeout, and the trace went on probing to 30 hops. The probe is the test's, so nothing is sent.

    private static readonly IPAddress FirstRouter = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress SecondRouter = IPAddress.Parse("198.51.100.2");

    [Theory]
    [InlineData(IPStatus.DestinationNetworkUnreachable)]
    [InlineData(IPStatus.DestinationHostUnreachable)]
    [InlineData(IPStatus.DestinationProhibited)]
    [InlineData(IPStatus.DestinationPortUnreachable)]
    [InlineData(IPStatus.DestinationUnreachable)]
    public async Task ARouterThatAnswersTheDestinationCannotBeReached_IsTheLastHop_WithItsAddress(IPStatus answer)
    {
        var probed = new List<int>();
        var tracer = new TracerouteService((_, ttl, _, _) =>
        {
            lock (probed) probed.Add(ttl);
            return Task.FromResult(ttl == 1
                ? new TracerouteService.ProbeReply(IPStatus.TtlExpired, FirstRouter)
                : new TracerouteService.ProbeReply(answer, SecondRouter));
        });

        var hops = await tracer.RunAsync("192.0.2.10", CancellationToken.None);

        Assert.Equal(2, hops.Count);
        Assert.Equal(("198.51.100.2", answer.ToString()), (hops[1].Address, hops[1].Status));
        Assert.NotNull(hops[1].LatencyMs);
        Assert.DoesNotContain(3, probed);
    }

    [Fact]
    public async Task AHopThatDoesNotAnswer_IsATimeout_AndTheTraceGoesOn()
    {
        // The probe seam leaves the rest as it was: a probe that fails is a timeout, and the next TTL is tried.
        var tracer = new TracerouteService((destination, ttl, _, _) => ttl == 1
            ? throw new PingException("no answer")
            : Task.FromResult(new TracerouteService.ProbeReply(IPStatus.Success, destination)));

        var hops = await tracer.RunAsync("192.0.2.10", CancellationToken.None);

        Assert.Equal([("*", "Timeout"), ("192.0.2.10", "Success")], hops.Select(h => (h.Address, h.Status)));
        Assert.Null(hops[0].LatencyMs);
    }

    [Theory]
    [InlineData("DestinationNetworkUnreachable", "it has no route to the destination's network")]
    [InlineData("DestinationHostUnreachable", "it cannot reach the destination")]
    [InlineData("DestinationUnreachable", "it cannot reach the destination")]
    [InlineData("DestinationProhibited", "the destination refused the trace")]
    [InlineData("DestinationProtocolUnreachable", "the destination refused the trace")]
    [InlineData("DestinationPortUnreachable", "the destination refused the trace")]
    [InlineData("TtlExpired", null)]
    [InlineData("Success", null)]
    [InlineData("Timeout", null)]
    public void ARefusal_IsDescribedInWords_AndNothingElseIs(string status, string? words)
        => Assert.Equal(words,
            TracerouteService.DescribeRefusal(new SysManager.Models.TracerouteHop { Status = status }));
}
