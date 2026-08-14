// SysManager · SpeedTestHistoryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Every test here points the service at its own throwaway directory via the <c>configDir</c> seam.
/// Before that seam existed the path was a <c>static readonly</c> built from
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> — which resolves through the Win32
/// known-folder API and ignores the <c>LOCALAPPDATA</c> environment variable — so these tests ran
/// against the user's real <c>speedtest-history.json</c>: one wrote fabricated results into it and
/// two deleted it. That also meant they could assert almost nothing, because the starting state was
/// whatever happened to be on the machine. With the directory under test control they can assert
/// exact content instead of merely "did not throw".
/// </summary>
public sealed class SpeedTestHistoryServiceTests : IDisposable
{
    private readonly string _dir;

    public SpeedTestHistoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone — nothing to clean up */ }
    }

    private SpeedTestHistoryService NewService() => new(_dir);

    private string HistoryFile => Path.Combine(_dir, "speedtest-history.json");

    private static SpeedTestResult Result(
        string engine = "HTTP", double down = 100.5, double up = 50.2, double ping = 12.3,
        string server = "test-server", DateTime? at = null)
        => new(engine, down, up, ping, server, at ?? new DateTime(2026, 1, 1, 12, 0, 0));

    [Fact]
    public void MaxPerEngine_Is20()
    {
        Assert.Equal(20, SpeedTestHistoryService.MaxPerEngine);
    }

    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsGenuinelyEmptyList()
    {
        // Now actually assertable: the directory is known-empty, so "empty" means empty rather than
        // "whatever this machine happened to have".
        using var svc = NewService();
        Assert.False(File.Exists(HistoryFile));

        var results = await svc.LoadAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task SaveAsync_WritesTheFile_AndCreatesTheDirectory()
    {
        // Delete the directory first so the Directory.CreateDirectory path in SaveAsync is exercised,
        // not just the write.
        Directory.Delete(_dir, recursive: true);
        using var svc = NewService();

        await svc.SaveAsync(Result());

        Assert.True(File.Exists(HistoryFile));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsEveryField()
    {
        using var svc = NewService();
        var at = new DateTime(2026, 3, 4, 5, 6, 7);
        await svc.SaveAsync(Result("HTTP", 123.4, 56.7, 8.9, "roundtrip-server", at));

        var loaded = await svc.LoadAsync();

        var only = Assert.Single(loaded);
        Assert.Equal("HTTP", only.Engine);
        Assert.Equal(123.4, only.DownloadMbps, precision: 3);
        Assert.Equal(56.7, only.UploadMbps, precision: 3);
        Assert.Equal(8.9, only.PingMs, precision: 3);
        Assert.Equal("roundtrip-server", only.Server);
        Assert.Equal(at, only.CompletedAt);
    }

    [Fact]
    public async Task ClearAsync_OneEngine_LeavesTheOtherEnginesResults()
    {
        // The discriminating case the old test could not express: clearing HTTP must not take Ookla
        // with it. Previously this only asserted "did not throw" — while deleting the user's history.
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP", 100, 10, 5, "http-server"));
        await svc.SaveAsync(Result("Ookla", 200, 20, 6, "ookla-server"));
        Assert.Equal(2, (await svc.LoadAsync()).Count);

        await svc.ClearAsync("HTTP");

        var remaining = await svc.LoadAsync();
        var only = Assert.Single(remaining);
        Assert.Equal("Ookla", only.Engine);
        Assert.Equal("ookla-server", only.Server);
    }

    [Fact]
    public async Task ClearAsync_OneEngine_IsCaseInsensitive()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP", server: "http-server"));

        await svc.ClearAsync("http");   // lower case — the service compares OrdinalIgnoreCase

        Assert.Empty(await svc.LoadAsync());
    }

    [Fact]
    public async Task ClearAsync_LastRemainingEngine_RemovesTheFileEntirely()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP"));
        Assert.True(File.Exists(HistoryFile));

        await svc.ClearAsync("HTTP");

        Assert.False(File.Exists(HistoryFile));   // no empty-array file left behind
        Assert.Empty(await svc.LoadAsync());
    }

    [Fact]
    public async Task ClearAsync_AllEngines_RemovesEverything()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP"));
        await svc.SaveAsync(Result("Ookla"));

        await svc.ClearAsync(null);

        Assert.False(File.Exists(HistoryFile));
        Assert.Empty(await svc.LoadAsync());
    }

    [Fact]
    public async Task ClearAsync_WhenNoFileExists_DoesNotThrow()
    {
        using var svc = NewService();
        Assert.False(File.Exists(HistoryFile));

        var ex = await Record.ExceptionAsync(() => svc.ClearAsync("HTTP"));

        Assert.Null(ex);
    }

    /// <summary>
    /// MaxPerEngine is 20; save 25 with increasing timestamps and assert the oldest 5 are dropped rather
    /// than the newest. Deterministic timestamps, no wall clock.
    /// <para>Asserts the WHOLE surviving window rather than spot-checking membership. The spot-check
    /// version failed once during a release with <c>DoesNotContain: filter matched</c> and a dumped
    /// collection — which took reading the raw dump to interpret. The window had slid down by exactly one
    /// entry (ending s5, s4 instead of s6, s5), meaning one save had been lost; the old
    /// <see cref="SpeedTestHistoryService.SaveAsync"/> swallowed the write failure and reported nothing,
    /// so the count assertion still passed and only the incidental <c>DoesNotContain("s4")</c> caught it.
    /// Every save is now checked for success as it happens, and the surviving set is compared exactly, so
    /// a dropped write names itself.</para>
    /// </summary>
    [Fact]
    public async Task SaveAsync_TrimsToMaxPerEngine_KeepingTheNewest()
    {
        using var svc = NewService();
        var start = new DateTime(2026, 1, 1, 0, 0, 0);
        for (int i = 0; i < 25; i++)
        {
            var saved = await svc.SaveAsync(Result("HTTP", down: i, server: $"s{i}", at: start.AddMinutes(i)));
            Assert.True(saved, $"save {i} (s{i}) failed to reach disk — every later assertion would be "
                             + "measuring a history with a hole in it.");
        }

        var loaded = await svc.LoadAsync();

        // Newest first, exactly s24 down to s5: 25 saved, 20 kept.
        var expected = Enumerable.Range(5, 20).Reverse().Select(i => $"s{i}").ToArray();
        Assert.Equal(expected, loaded.Select(r => r.Server).ToArray());
    }

    [Fact]
    public async Task SaveAsync_TrimsPerEngineIndependently()
    {
        // The trim groups by engine, so 20 HTTP results must not evict Ookla's.
        using var svc = NewService();
        var start = new DateTime(2026, 1, 1, 0, 0, 0);
        for (int i = 0; i < 22; i++)
            await svc.SaveAsync(Result("HTTP", server: $"h{i}", at: start.AddMinutes(i)));
        await svc.SaveAsync(Result("Ookla", server: "ookla-kept", at: start.AddMinutes(100)));

        var loaded = await svc.LoadAsync();

        Assert.Equal(SpeedTestHistoryService.MaxPerEngine + 1, loaded.Count);
        Assert.Contains(loaded, r => r.Server == "ookla-kept");
        Assert.Equal(SpeedTestHistoryService.MaxPerEngine, loaded.Count(r => r.Engine == "HTTP"));
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsMalformed_ReturnsEmptyRatherThanThrowing()
    {
        // A truncated or hand-edited file must not take the tab down. This is a real failure path the
        // old tests could not reach, because they never controlled the file's contents.
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        using var svc = NewService();

        var results = await svc.LoadAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsJsonNull_ReturnsEmpty()
    {
        // Deserialize returns null for the literal "null"; the service guards that explicitly.
        await File.WriteAllTextAsync(HistoryFile, "null");
        using var svc = NewService();

        Assert.Empty(await svc.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_MissingEngineField_DefaultsToHttp()
    {
        // The DTO's Engine is nullable and the loader coalesces to "HTTP"; pin that so the fallback
        // cannot silently change and mis-bucket old entries.
        await File.WriteAllTextAsync(HistoryFile,
            """[{"downloadMbps":10,"uploadMbps":2,"pingMs":5,"server":"s","completedAt":"2026-01-01T00:00:00"}]""");
        using var svc = NewService();

        var only = Assert.Single(await svc.LoadAsync());

        Assert.Equal("HTTP", only.Engine);
        Assert.Equal("s", only.Server);
    }

    [Fact]
    public async Task TwoServices_OnDifferentDirectories_DoNotSeeEachOther()
    {
        // Proves the seam actually isolates: the whole point of the fix.
        var otherDir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDir);
        try
        {
            using var a = NewService();
            using var b = new SpeedTestHistoryService(otherDir);

            await a.SaveAsync(Result("HTTP", server: "in-a"));

            Assert.Single(await a.LoadAsync());
            Assert.Empty(await b.LoadAsync());
        }
        finally
        {
            Directory.Delete(otherDir, recursive: true);
        }
    }
}
