// SysManager · TracerouteServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
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
        // Regression (P2 #39): the destination is resolved ONCE up front and every
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
}
