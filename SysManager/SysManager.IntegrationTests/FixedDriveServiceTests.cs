// SysManager · FixedDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
    public async Task EnumerateSync_ReturnsSameAsAsync()
    {
        var s = new FixedDriveService();
        var sync = FixedDriveService.Enumerate();
        var asyncResult = await s.EnumerateAsync();
        Assert.Equal(sync.Count, asyncResult.Count);
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

    // ---------- P2 #33 regression: DriveFormat IOException must not abort all drives ----------

    [Fact]
    public void Enumerate_DoesNotThrow_WhenDriveFormatAccessible()
    {
        // Contract: Enumerate() must never throw — it degrades by skipping individual
        // drives whose properties are inaccessible. Before the fix, DriveFormat was read
        // in a LINQ .Where() predicate (lazy evaluation during MoveNext), which threw
        // OUTSIDE the per-drive try/catch and aborted enumeration of ALL remaining drives
        // when a single volume was BitLocker-locked or transiently busy.
        var ex = Record.Exception(() => FixedDriveService.Enumerate());
        Assert.Null(ex);
    }

    [Fact]
    public void Enumerate_ReturnsOnlyNtfsOrRefs()
    {
        // Post-fix: the format filter still works correctly (just moved inside try).
        var drives = FixedDriveService.Enumerate();
        Assert.All(drives, d =>
        {
            var fs = (d.FileSystem ?? "").ToUpperInvariant();
            Assert.True(fs is "NTFS" or "REFS", $"Unexpected FS '{fs}' on {d.Letter}");
        });
    }
}
