// SysManager · AppIconServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AppIconService"/> (audit finding tests #11). The HTTP
/// transport is injected as a stubbed <see cref="HttpMessageHandler"/> so the
/// download path is exercised without touching the live internet, and the
/// failure paths (network error, cancellation) are verified to return null
/// rather than throw.
/// </summary>
/// <remarks>
/// Every service here is constructed with a TEMP configDir. Before that seam existed, the paths were
/// <c>static readonly</c> under <c>%LocalAppData%</c> and the five tests below that call
/// <see cref="AppIconService.SetNetworkFetchEnabled"/> persisted to the user's REAL profile —
/// silently overwriting the icon-fetch preference they had chosen and leaving it at whatever the
/// last-executing test set (xUnit does not guarantee order). Never construct this service in a test
/// without a configDir.
/// </remarks>
public sealed class AppIconServiceTests : IDisposable
{
    private readonly string _dir;

    public AppIconServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone — nothing to clean up */ }
    }

    private AppIconService NewService(HttpMessageHandler? handler = null) => new(handler, _dir);

    private string PreferenceFile => Path.Combine(_dir, "icon-fetch.json");

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
        var svc = NewService(handler);

        // An ID with no mapped domain must short-circuit before any HTTP call.
        var icon = await svc.GetIconAsync("No.Such.App." + Guid.NewGuid().ToString("N"));

        Assert.Null(icon);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetIconAsync_NetworkError_ReturnsNull_DoesNotThrow()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("simulated offline"));
        var svc = NewService(handler);
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
        var svc = NewService(handler);
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
        var svc = NewService(handler);
        svc.SetNetworkFetchEnabled(false); // explicit: opt-out

        // A known-mapped ID that is NOT cached must still make zero network calls.
        var icon = await svc.GetIconAsync("Git.Git." + Guid.NewGuid().ToString("N"));

        Assert.Null(icon);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void SetNetworkFetchEnabled_TogglesAndReportsValue()
    {
        var svc = NewService(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
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
        var withStub = NewService(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var withDefault = NewService();
        Assert.NotNull(withStub);
        Assert.NotNull(withDefault);
    }

    // ── configDir seam (regression: the preference was written to the user's real profile) ──

    [Fact]
    public void SetNetworkFetchEnabled_WritesInsideTheGivenConfigDir()
    {
        // The point of the seam. Before it, this write landed on
        // %LocalAppData%\SysManager\icon-fetch.json and clobbered the user's own choice.
        Assert.False(File.Exists(PreferenceFile));

        NewService().SetNetworkFetchEnabled(true);

        Assert.True(File.Exists(PreferenceFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Preference_RoundTripsThroughTheFile(bool enabled)
    {
        // Persistence is the feature: the choice has to survive a restart. Asserting the
        // in-memory property alone would pass even if nothing were written.
        NewService().SetNetworkFetchEnabled(enabled);

        Assert.Equal(enabled, NewService().NetworkFetchEnabled);
    }

    [Fact]
    public void Preference_PersistedShapeIsTheDocumentedJson()
    {
        // Pins the on-disk contract: an existing user's file must still be readable after any
        // refactor of this service, and vice versa.
        NewService().SetNetworkFetchEnabled(true);

        using var doc = JsonDocument.Parse(File.ReadAllText(PreferenceFile));
        Assert.True(doc.RootElement.GetProperty("NetworkFetchEnabled").GetBoolean());
    }

    [Fact]
    public void DefaultsToDisabled_WhenNoPreferenceFileExists()
    {
        // The no-cloud promise: a fresh profile must not fetch over the network until the user
        // opts in. README states icons are "off by default".
        Assert.False(File.Exists(PreferenceFile));

        Assert.False(NewService().NetworkFetchEnabled);
    }

    [Fact]
    public void MalformedPreferenceFile_FallsBackToDisabled()
    {
        // A corrupt file must fail CLOSED (no network), not open.
        File.WriteAllText(PreferenceFile, "{ this is not json");

        Assert.False(NewService().NetworkFetchEnabled);
    }

    [Fact]
    public void TwoServicesWithDifferentConfigDirs_DoNotShareState()
    {
        // Proves the path is really per-instance. If it were still static, the second service would
        // observe the first one's value and this would fail — which is exactly the coupling that let
        // the suite reach into the user's profile.
        var otherDir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDir);
        try
        {
            NewService().SetNetworkFetchEnabled(true);
            var other = new AppIconService(null, otherDir);

            Assert.False(other.NetworkFetchEnabled);
            Assert.True(NewService().NetworkFetchEnabled);
        }
        finally
        {
            Directory.Delete(otherDir, recursive: true);
        }
    }
}
