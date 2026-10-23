// SysManager · UpdateServiceUnreadableReplyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A reply from GitHub that arrives but cannot be read leaves the update check and the release list empty-handed,
/// never throwing.
/// </summary>
/// <remarks>
/// Past a failure to connect, reading the body fails in four ways: cut off part way, an <c>IOException</c>; a gzip
/// body that does not decompress, an <c>InvalidDataException</c>; a Brotli one, an <c>InvalidOperationException</c>;
/// and JSON that does not parse. <c>GetRecentAsync</c> caught only the last, so the other three reached About as an
/// error dialog (#2663). Each reply here is what the app's client hands the JSON reader in that case: the
/// decompressing stream it wraps a compressed body in, or a body whose connection drops after its first bytes.
/// </remarks>
public class UpdateServiceUnreadableReplyTests
{
    /// <summary>An update service whose every reply from GitHub fails to read in the given way.</summary>
    internal static UpdateService ServiceWhoseReplyFails(string failure) => new(new FailingReply(failure));

    [Theory]
    [InlineData("cut off")]
    [InlineData("gzip")]
    [InlineData("brotli")]
    [InlineData("not JSON")]
    public async Task GetRecentAsync_WhenTheReplyCannotBeRead_SaysSoRatherThanAnsweringNoReleases(string failure)
    {
        var releases = await ServiceWhoseReplyFails(failure).GetRecentAsync(10);

        // Null, not an empty list: About shows "GitHub could not be reached" and keeps the notes it has, where an
        // empty list would read as "this repository has no releases" and replace them with nothing (#2831).
        Assert.Null(releases);
    }

    /// <summary>
    /// A connection that never reached GitHub answers "could not be read", not "no releases".
    /// </summary>
    /// <remarks>
    /// The branch About's own test could not reach: it substitutes the service and throws from there, so the
    /// service's own network catch had nothing covering it, and returning an empty list from it would have
    /// erased the notes on screen exactly as before (#2831).
    /// </remarks>
    [Fact]
    public async Task GetRecentAsync_WhenGitHubCannotBeReached_SaysSoRatherThanAnsweringNoReleases()
    {
        var service = new UpdateService(new RefusesToConnect());

        Assert.Null(await service.GetRecentAsync(10));
    }

    /// <summary>A reply that is literally <c>null</c> is also "could not be read".</summary>
    /// <remarks>Its own branch in the service, and the one the two above never reach.</remarks>
    [Fact]
    public async Task GetRecentAsync_WhenTheReplyIsJsonNull_SaysSoRatherThanAnsweringNoReleases()
    {
        var service = new UpdateService(new ReplyIs("null"));

        Assert.Null(await service.GetRecentAsync(10));
    }

    /// <summary>An empty list of releases is an answer, and stays one.</summary>
    /// <remarks>
    /// The negative control for both: "this repository has no releases" must not become "could not be read", or
    /// the section would claim a connection problem that is not there.
    /// </remarks>
    [Fact]
    public async Task GetRecentAsync_WhenThereAreNoReleases_AnswersWithNone()
    {
        var service = new UpdateService(new ReplyIs("[]"));

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<UpdateService.ReleaseInfo>>(
            await service.GetRecentAsync(10)));
    }

    /// <summary>Fails to connect at all, as a machine with no network does.</summary>
    private sealed class RefusesToConnect : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known."));
    }

    /// <summary>Answers every request with a 200 whose body is exactly <paramref name="json"/>.</summary>
    private sealed class ReplyIs(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    [Theory]
    [InlineData("cut off", "HttpIOException")]
    [InlineData("gzip", "InvalidDataException")]
    [InlineData("brotli", "InvalidOperationException")]
    [InlineData("not JSON", "JsonException")]
    public async Task GetLatestAsync_WhenTheReplyCannotBeRead_ReturnsNothingAndSaysWhy(string failure, string reason)
    {
        var service = ServiceWhoseReplyFails(failure);

        var latest = await service.GetLatestAsync();

        Assert.Null(latest);
        Assert.StartsWith($"Unexpected: {reason}: ", service.LastError, StringComparison.Ordinal);
    }

    /// <summary>Answers every request with a 200 whose body fails to read in one of the four ways.</summary>
    private sealed class FailingReply(string failure) : HttpMessageHandler
    {
        // A gzip header and then bytes no decoder accepts, so gzip and Brotli both fail on the first read.
        private static readonly byte[] Undecodable =
            [0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xff, 0xfe, 0xfd, 0xfc, 0x01, 0x02];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The opening of the shape each call reads, so a cut-off body fails on the read and not on the shape.
            var opening = request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                ? "{\"tag_name\":\"v1."
                : "[{\"tag_name\":\"v1.";
            Stream body = failure switch
            {
                "cut off" => new CutOffAfter(System.Text.Encoding.UTF8.GetBytes(opening)),
                "gzip" => new GZipStream(new MemoryStream(Undecodable), CompressionMode.Decompress),
                "brotli" => new BrotliStream(new MemoryStream(Undecodable), CompressionMode.Decompress),
                "not JSON" => new MemoryStream("<html>"u8.ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "not one of the four failures"),
            };
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>
    /// A body whose connection drops after <paramref name="opening"/>, reported as the app's client reports it.
    /// </summary>
    private sealed class CutOffAfter(byte[] opening) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_sent) throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            _sent = true;
            opening.CopyTo(buffer);
            return opening.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return ValueTask.FromResult(Read(buffer.Span)); }
            catch (HttpIOException ex) { return ValueTask.FromException<int>(ex); }
        }
    }
}
