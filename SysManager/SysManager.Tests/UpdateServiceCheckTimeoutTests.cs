// SysManager · UpdateServiceCheckTimeoutTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.Http;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A check that times out is retried once and says GitHub did not answer, and only the caller's own cancel reads as a
/// cancel.
/// </summary>
/// <remarks>
/// The client's timeout arrives as the same exception as a cancel, and every <c>OperationCanceledException</c> was
/// taken for one: "Check cancelled.", with no retry. No caller passes a token, so the timeout was the only way there
/// (#2728). Each handler here answers the way the client does when its timeout fires: a
/// <see cref="TaskCanceledException"/> with a <see cref="TimeoutException"/> inside.
/// </remarks>
public class UpdateServiceCheckTimeoutTests
{
    private const string ReleaseJson =
        """{"tag_name":"v99.1.0","name":"SysManager 99.1.0","body":"notes","html_url":"https://github.com/laurentiu021/SystemManager/releases/tag/v99.1.0"}""";

    /// <summary>Times out on the first <paramref name="timeouts"/> requests, then answers with a release.</summary>
    private sealed class TimesOut(int timeouts) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (++Requests <= timeouts)
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.",
                    new TimeoutException("The operation was canceled.")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task ATimeout_IsRetriedOnce_AndTheAnswerAfterItIsTheRelease()
    {
        var handler = new TimesOut(timeouts: 1);
        var service = new UpdateService(handler);

        var latest = await service.GetLatestAsync();

        Assert.NotNull(latest);
        Assert.Equal("v99.1.0", latest.Tag);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("", service.LastError);
    }

    [Fact]
    public async Task TwoTimeouts_SayGitHubDidNotAnswer_NotThatTheCheckWasCancelled()
    {
        var handler = new TimesOut(timeouts: 2);
        var service = new UpdateService(handler);

        Assert.Null(await service.GetLatestAsync());

        Assert.Equal("GitHub did not answer in time.", service.LastError);
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>Cancels the caller's own token as the request goes out, as a user's cancel would.</summary>
    private sealed class CancelledByTheCaller(CancellationTokenSource caller) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(caller.Token);
        }
    }

    [Fact]
    public async Task TheCallersOwnCancel_IsACancel_AndIsNotRetried()
    {
        using var caller = new CancellationTokenSource();
        var handler = new CancelledByTheCaller(caller);
        var service = new UpdateService(handler);

        Assert.Null(await service.GetLatestAsync(caller.Token));

        Assert.Equal("Check cancelled.", service.LastError);
        Assert.Equal(1, handler.Requests);
    }
}
