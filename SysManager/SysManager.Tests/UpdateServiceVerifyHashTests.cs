// SysManager · UpdateServiceVerifyHashTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The check Install makes before it starts a downloaded update: the file's SHA-256 against the .sha256 published
/// beside the release. Only a match may say verified; anything that keeps the check from being made says it was not.
/// </summary>
/// <remarks>
/// The tests that stood here hashed bytes with <see cref="SHA256"/> themselves and never called the service, and
/// About's own tests substitute the check, so a check that answered "verified" for everything left the whole suite
/// green (#2741). These run the real service over a stand-in for GitHub.
/// </remarks>
public class UpdateServiceVerifyHashTests
{
    private static readonly UpdateService.ReleaseInfo Release = new(
        new Version(99, 0, 0), "v99.0.0", "SysManager 99.0.0", "Notes.", DateTimeOffset.UnixEpoch,
        "https://github.com/laurentiu021/SystemManager/releases/tag/v99.0.0", AssetUrl: null, AssetSize: null);

    private static readonly byte[] Download = "the bytes of the downloaded update"u8.ToArray();
    private static readonly string DownloadHash = Convert.ToHexString(SHA256.HashData(Download));

    /// <summary>A .sha256 as the release publishes it: the hash, then the file it is for.</summary>
    private static string Published(string hash) => $"{hash} *SysManager-v99.0.0.exe";

    private static Task<(bool Verified, string? ExpectedHash, string? ActualHash)> VerifyAsync(
        HttpMessageHandler github, Stream file, CancellationToken ct = default) =>
        new UpdateService(github).VerifyHashAsync(Release, file, ct);

    [Fact]
    public async Task AFileThatMatchesThePublishedHash_IsVerified()
    {
        var result = await VerifyAsync(new Answers(Published(DownloadHash)), new MemoryStream(Download));

        Assert.Equal((true, DownloadHash, DownloadHash), result);
    }

    [Fact]
    public async Task AHashPublishedInLowerCase_StillMatches()
    {
        var lower = DownloadHash.ToLowerInvariant();

        var result = await VerifyAsync(new Answers(Published(lower)), new MemoryStream(Download));

        Assert.Equal((true, lower, DownloadHash), result);
    }

    [Fact]
    public async Task AFileThatDoesNotMatch_IsRefused_WithBothHashes()
    {
        // Both, because About says a mismatch only when it has both, and lets the file go.
        var other = Convert.ToHexString(SHA256.HashData("another file"u8));

        var result = await VerifyAsync(new Answers(Published(other)), new MemoryStream(Download));

        Assert.Equal((false, other, DownloadHash), result);
    }

    [Fact]
    public async Task AStreamThatWasReadToItsEnd_IsHashedFromTheStart()
    {
        var file = new MemoryStream(Download);
        file.Seek(0, SeekOrigin.End);

        var result = await VerifyAsync(new Answers(Published(DownloadHash)), file);

        Assert.True(result.Verified);
    }

    [Fact]
    public async Task TheHashItChecksAgainst_IsTheOnePublishedBesideThisRelease()
    {
        var github = new Answers(Published(DownloadHash));

        await VerifyAsync(github, new MemoryStream(Download));

        var asked = Assert.Single(github.Asked);
        Assert.Equal(
            $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases/download/v99.0.0/SysManager-v99.0.0.exe.sha256",
            asked.AbsoluteUri);
    }

    // ── What keeps the check from being made: never "verified", and nothing to call a mismatch ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<!DOCTYPE html>")]   // a page instead of the file, as a captive portal serves
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE *SysManager-v99.0.0.exe")]   // 63
    public async Task APublishedHashThatCannotBeRead_IsAFailureToCheck(string published)
    {
        var result = await VerifyAsync(new Answers(published), new MemoryStream(Download));

        Assert.Equal((false, null, null), result);
    }

    [Fact]
    public async Task AReleaseWithNoPublishedHash_IsAFailureToCheck()
    {
        var result = await VerifyAsync(new Answers("Not Found", HttpStatusCode.NotFound), new MemoryStream(Download));

        Assert.Equal((false, null, null), result);
    }

    [Fact]
    public async Task WhenGitHubCannotBeReached_ItIsAFailureToCheck()
    {
        var result = await VerifyAsync(new RefusesToConnect(), new MemoryStream(Download));

        Assert.Equal((false, null, null), result);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsAFailureToCheck()
    {
        var result = await VerifyAsync(new Answers(Published(DownloadHash)), new UnreadableFile());

        Assert.Equal((false, null, null), result);
    }

    [Fact]
    public async Task WhenCancelled_ItIsAFailureToCheck()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await VerifyAsync(new Answers(Published(DownloadHash)), new MemoryStream(Download), cancelled.Token);

        Assert.Equal((false, null, null), result);
    }

    /// <summary>Answers every request with <paramref name="body"/>, and keeps what was asked for.</summary>
    private sealed class Answers(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body), RequestMessage = request });
        }
    }

    /// <summary>Fails to connect at all, as a machine with no network does.</summary>
    private sealed class RefusesToConnect : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known."));
    }

    /// <summary>An open file whose reads fail, as a disk error fails them.</summary>
    private sealed class UnreadableFile() : MemoryStream(new byte[1024])
    {
        public override int Read(byte[] buffer, int offset, int count) => throw ReadFailed();
        public override int Read(Span<byte> buffer) => throw ReadFailed();

        private static IOException ReadFailed() =>
            new("The request could not be performed because of an I/O device error.");
    }
}
