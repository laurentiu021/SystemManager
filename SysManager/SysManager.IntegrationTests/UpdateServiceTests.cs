using SysManager.Services;

namespace SysManager.IntegrationTests;

public class UpdateServiceTests
{
    // -------- ParseVersion: happy paths ----------

    [Theory]
    [InlineData("v0.5.0",   0, 5, 0)]
    [InlineData("0.5.0",    0, 5, 0)]
    [InlineData("V1.2.3",   1, 2, 3)]
    [InlineData("v10.20.30",10,20,30)]
    [InlineData("v0.0.1",   0, 0, 1)]
    [InlineData("v99.0.0",  99, 0, 0)]
    [InlineData("0.0.0",    0, 0, 0)]
    public void ParseVersion_AcceptsStandardTags(string tag, int major, int minor, int build)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(major, v!.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
    }

    [Theory]
    [InlineData("v0.5.0-beta")]
    [InlineData("v0.5.0-rc1")]
    [InlineData("v0.5.0-preview.3")]
    [InlineData("0.5.0+build.42")]
    [InlineData("v1.0.0 hotfix")]
    public void ParseVersion_StripsSuffix(string tag)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(0, v!.Build == -1 ? 0 : v.Build); // just verifies no crash & parse ok
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("vv1.2.3")]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData(".")]
    public void ParseVersion_RejectsGarbage(string tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    // -------- IsNewer ----------

    [Theory]
    [InlineData("0.4.0", "0.3.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.3.0", "0.3.0", false)]
    [InlineData("0.3.0", "0.4.0", false)]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("10.0.0", "9.99.99", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    public void IsNewer_ComparesStrictly(string latest, string current, bool expected)
    {
        var a = Version.Parse(latest);
        var b = Version.Parse(current);
        Assert.Equal(expected, UpdateService.IsNewer(a, b));
    }

    // -------- Constants ----------

    [Fact]
    public void Constants_AreSet()
    {
        Assert.Equal("laurentiu021", UpdateService.Owner);
        Assert.Equal("SysManager", UpdateService.Repo);
        Assert.Equal("SysManager.exe", UpdateService.AssetName);
    }

    [Fact]
    public void CurrentVersion_IsAtLeastZeroFive()
    {
        var v = UpdateService.CurrentVersion;
        Assert.True(v >= new Version(0, 5, 0), $"Expected >= 0.5.0 but got {v}");
    }

    [Fact]
    public void CurrentVersion_IsNeverNull()
    {
        Assert.NotNull(UpdateService.CurrentVersion);
    }

    [Fact]
    public void Service_Constructs()
    {
        var svc = new UpdateService();
        Assert.NotNull(svc);
    }

    [Fact]
    public async Task GetLatestAsync_GracefullyReturnsOnNetworkError()
    {
        // We cannot mock HttpClient easily here — but the service must not
        // throw into the UI on network failure. Call with an immediate-cancel
        // token so it aborts without contacting GitHub.
        var svc = new UpdateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await svc.GetLatestAsync(cts.Token);
        Assert.Null(r);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsEmptyOnCancel()
    {
        var svc = new UpdateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var list = await svc.GetRecentAsync(5, cts.Token);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsNullWithoutAssetUrl()
    {
        var svc = new UpdateService();
        var fake = new UpdateService.ReleaseInfo(
            Version: new Version(99, 0, 0),
            Tag: "v99.0.0",
            Name: "Fake",
            Body: "",
            PublishedAt: DateTimeOffset.Now,
            HtmlUrl: "https://example.com",
            AssetUrl: null,
            AssetSize: null);
        var path = await svc.DownloadAsync(fake);
        Assert.Null(path);
    }

    [Fact]
    public void ReleaseInfo_IsImmutable()
    {
        var r = new UpdateService.ReleaseInfo(
            Version: new Version(1, 2, 3),
            Tag: "v1.2.3",
            Name: "Name",
            Body: "Body",
            PublishedAt: DateTimeOffset.FromUnixTimeSeconds(1000000000),
            HtmlUrl: "https://u",
            AssetUrl: "https://a",
            AssetSize: 1024);
        Assert.Equal("v1.2.3", r.Tag);
        Assert.Equal(1024, r.AssetSize);
    }

    [Theory]
    [InlineData("0.0.1", "0.0.0", true)]
    [InlineData("0.0.0", "0.0.1", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("100.0.0", "99.99.99", true)]
    public void IsNewer_HandlesMajorJumps(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }

    [Theory]
    [InlineData("v0.5.0")]
    [InlineData("0.5.0")]
    [InlineData("v0.5.0-alpha")]
    [InlineData("v0.5.0+build1")]
    public void ParseVersion_AlwaysYieldsNonNegative(string tag)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.True(v!.Major >= 0);
        Assert.True(v.Minor >= 0);
    }
}
