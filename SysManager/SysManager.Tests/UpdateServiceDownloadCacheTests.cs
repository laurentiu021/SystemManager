// SysManager · UpdateServiceDownloadCacheTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A downloaded update is kept with its hash beside it, used again only while it still matches that hash, and a
/// download that fails leaves nothing behind.
/// </summary>
/// <remarks>
/// The cache check and the clean-up after a refused download had no test: the only test that called the download
/// passed no asset and returned on its first line, and the stall tests cover a body that stops arriving (#2741). The
/// folder is a throwaway one and GitHub a stand-in.
/// </remarks>
public sealed class UpdateServiceDownloadCacheTests : IDisposable
{
    private static readonly byte[] Build = "the bytes of SysManager 99.0.0"u8.ToArray();

    private readonly string _dir = Directory.CreateTempSubdirectory("UpdateCache_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Target => Path.Join(_dir, "SysManager-99.0.0.exe");

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static UpdateService.ReleaseInfo Release(
        string? assetUrl = "https://github.com/laurentiu021/SystemManager/releases/download/v99.0.0/SysManager-v99.0.0.exe")
        => new(new Version(99, 0, 0), "v99.0.0", "SysManager 99.0.0", "Notes.", DateTimeOffset.UnixEpoch,
            "https://github.com/laurentiu021/SystemManager/releases/tag/v99.0.0", assetUrl, Build.Length);

    private UpdateService Service(HttpMessageHandler github) => new(github, downloadDir: _dir);

    [Fact]
    public async Task ACompletedDownload_IsKeptWithItsHashBesideIt()
    {
        var path = await Service(new Serves(Build)).DownloadAsync(Release());

        Assert.Equal(Target, path);
        Assert.Equal(Build, await File.ReadAllBytesAsync(Target));
        Assert.Equal(HashOf(Build), await File.ReadAllTextAsync(Target + ".sha256"));
        Assert.False(File.Exists(Target + ".tmp"));
    }

    [Fact]
    public async Task AKeptDownloadThatStillMatchesItsHash_IsUsedWithoutDownloadingAgain()
    {
        await File.WriteAllBytesAsync(Target, Build);
        await File.WriteAllTextAsync(Target + ".sha256", HashOf(Build));
        var github = new Serves("a newer upload"u8.ToArray());

        var path = await Service(github).DownloadAsync(Release());

        Assert.Equal(Target, path);
        Assert.Empty(github.Asked);
        Assert.Equal(Build, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task AKeptDownloadThatNoLongerMatchesItsHash_IsDownloadedAgain()
    {
        // Changed since it was kept: the folder is the user's own, so anything running as the user can write it.
        await File.WriteAllBytesAsync(Target, "changed since it was kept"u8.ToArray());
        await File.WriteAllTextAsync(Target + ".sha256", HashOf(Build));
        var github = new Serves(Build);

        var path = await Service(github).DownloadAsync(Release());

        Assert.Equal(Target, path);
        Assert.Single(github.Asked);
        Assert.Equal(Build, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task AKeptDownloadWithNoHashBesideIt_IsDownloadedAgain()
    {
        await File.WriteAllBytesAsync(Target, Build);
        var github = new Serves(Build);

        await Service(github).DownloadAsync(Release());

        Assert.Single(github.Asked);
        Assert.Equal(HashOf(Build), await File.ReadAllTextAsync(Target + ".sha256"));
    }

    [Fact]
    public async Task ADownloadTheServerRefuses_ReturnsNothing_AndLeavesNothingBehind()
    {
        var path = await Service(new Serves([], HttpStatusCode.NotFound)).DownloadAsync(Release());

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AReleaseWithNoFileToDownload_AsksForNothing()
    {
        var github = new Serves(Build);

        Assert.Null(await Service(github).DownloadAsync(Release(assetUrl: null)));
        Assert.Empty(github.Asked);
    }

    /// <summary>Answers every request with <paramref name="body"/>, and keeps what was asked for.</summary>
    private sealed class Serves(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }
}
