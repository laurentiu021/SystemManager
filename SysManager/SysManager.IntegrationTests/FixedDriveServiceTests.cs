// SysManager · FixedDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

public class FixedDriveServiceTests
{
    [Fact]
    public void Constructs()
    {
        var s = new FixedDriveService();
        Assert.NotNull(s);
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsNonNull()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.NotNull(r);
    }

    [Fact]
    public async Task EnumerateAsync_AtLeastOneDrive_OnDev()
    {
        // The dev machine will always have at least C:, so this should be true in practice.
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task EnumerateAsync_AllDrivesHaveLetter()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d => Assert.False(string.IsNullOrWhiteSpace(d.Letter)));
    }

    [Fact]
    public async Task EnumerateAsync_AllDrivesHaveValidLetter()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d => Assert.Matches(@"^[A-Z]:$", d.Letter));
    }

    [Fact]
    public async Task EnumerateAsync_SizeNonNegative()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d => Assert.True(d.SizeGB >= 0));
    }

    [Fact]
    public async Task EnumerateAsync_FreeSizeDoesNotExceedTotal()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d => Assert.True(d.FreeGB <= d.SizeGB + 1, $"{d.Letter}: free={d.FreeGB} > size={d.SizeGB}"));
    }

    [Fact]
    public async Task EnumerateAsync_FileSystem_IsNtfsOrRefs()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d =>
        {
            var fs = (d.FileSystem ?? "").ToUpperInvariant();
            Assert.True(fs == "NTFS" || fs == "REFS", $"Unexpected FS '{fs}'");
        });
    }

    [Fact]
    public async Task EnumerateAsync_HasCDrive()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        // Windows-hosted dev environment — C: will always be present.
        Assert.Contains(r, d => string.Equals(d.Letter, "C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnumerateAsync_UniqueLetters()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        var letters = r.Select(d => d.Letter).ToList();
        Assert.Equal(letters.Count, letters.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EnumerateSync_ReturnsSameAsAsync()
    {
        var s = new FixedDriveService();
        var sync = FixedDriveService.Enumerate();
        var async = s.EnumerateAsync().Result;
        Assert.Equal(sync.Count, async.Count);
    }

    [Fact]
    public async Task EnumerateAsync_CancelledToken_StillReturns()
    {
        var s = new FixedDriveService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await s.EnumerateAsync(cts.Token);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task EnumerateAsync_LabelIsPresent()
    {
        var s = new FixedDriveService();
        var r = await s.EnumerateAsync();
        Assert.All(r, d => Assert.False(string.IsNullOrWhiteSpace(d.Label)));
    }
}
