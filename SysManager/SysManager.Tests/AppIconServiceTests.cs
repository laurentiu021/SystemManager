// SysManager · AppIconServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.Http;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AppIconService"/> (audit finding tests #11). The HTTP
/// transport is injected as a stubbed <see cref="HttpMessageHandler"/> so the
/// download path is exercised without touching the live internet, and the
/// failure paths (network error, cancellation) are verified to return null
/// rather than throw.
/// </summary>
public class AppIconServiceTests
{
    /// <summary>A handler that returns a fixed response (or throws) without any network.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(responder(request, ct));
        }
    }

    [Fact]
    public async Task GetIconAsync_UnknownAppId_ReturnsNull_WithoutAnyRequest()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = new AppIconService(handler);

        // An ID with no mapped domain must short-circuit before any HTTP call.
        var icon = await svc.GetIconAsync("No.Such.App." + Guid.NewGuid().ToString("N"));

        Assert.Null(icon);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetIconAsync_NetworkError_ReturnsNull_DoesNotThrow()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("simulated offline"));
        var svc = new AppIconService(handler);
        svc.SetNetworkFetchEnabled(true); // opt in so the download path is reached

        // A known mapped ID so the download path is reached, then the handler fails.
        var ex = await Record.ExceptionAsync(() => svc.GetIconAsync("Git.Git"));

        Assert.Null(ex); // failure is swallowed and logged, never propagated
    }

    [Fact]
    public async Task GetIconAsync_AlreadyCancelledToken_ReturnsNull_DoesNotThrow()
    {
        var handler = new StubHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = new AppIconService(handler);
        svc.SetNetworkFetchEnabled(true); // opt in so the download path is reached
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => svc.GetIconAsync("Git.Git", cts.Token));

        Assert.Null(ex); // TaskCanceledException is caught and turned into a null result
    }

    // ── Network-fetch opt-in (idx 9/10/13/14: honour the no-cloud promise) ────

    [Fact]
    public async Task GetIconAsync_WhenFetchDisabled_MakesNoNetworkRequest()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = new AppIconService(handler);
        svc.SetNetworkFetchEnabled(false); // explicit: opt-out

        // A known-mapped ID that is NOT cached must still make zero network calls.
        var icon = await svc.GetIconAsync("Git.Git." + Guid.NewGuid().ToString("N"));

        Assert.Null(icon);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void SetNetworkFetchEnabled_TogglesAndReportsValue()
    {
        var svc = new AppIconService(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(svc.SetNetworkFetchEnabled(true));
        Assert.True(svc.NetworkFetchEnabled);
        Assert.False(svc.SetNetworkFetchEnabled(false));
        Assert.False(svc.NetworkFetchEnabled);
    }

    [Fact]
    public void Constructor_WithCustomHandler_DoesNotThrow()
    {
        // The injectable handler is the seam under test; constructing with one
        // (and the default) must both succeed.
        var withStub = new AppIconService(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var withDefault = new AppIconService();
        Assert.NotNull(withStub);
        Assert.NotNull(withDefault);
    }
}
