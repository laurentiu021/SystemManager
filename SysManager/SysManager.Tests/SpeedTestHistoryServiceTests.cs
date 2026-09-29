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

    // The history as a reader sees it once the file could be read. Null is its own answer, "the file could not be
    // read", and only the tests about that ask for it (#2521).
    private static async Task<List<SpeedTestResult>> LoadedAsync(SpeedTestHistoryService svc)
    {
        var results = await svc.LoadAsync();
        Assert.NotNull(results);
        return results;
    }

    private static SpeedTestResult Result(
        string engine = "HTTP", double down = 100.5, double? up = 50.2, double? ping = 12.3,
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

        var results = await LoadedAsync(svc);

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

        var loaded = await LoadedAsync(svc);

        var only = Assert.Single(loaded);
        Assert.Equal("HTTP", only.Engine);
        Assert.Equal(123.4, only.DownloadMbps, precision: 3);
        Assert.NotNull(only.UploadMbps);
        Assert.Equal(56.7, only.UploadMbps.Value, precision: 3);
        Assert.NotNull(only.PingMs);
        Assert.Equal(8.9, only.PingMs.Value, precision: 3);
        Assert.Equal("roundtrip-server", only.Server);
        Assert.Equal(at, only.CompletedAt);
    }

    // ---------- an upload or ping that was not measured (#2504) ----------

    [Fact]
    public async Task SaveAndLoad_AnUploadAndPingThatWereNotMeasured_ComeBackNotMeasured()
    {
        // They used to be saved as 0, and 0 ms is a perfect ping: the history kept the best reading there is for a
        // test whose ping got no answer.
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP", down: 123.4, up: null, ping: null, server: "unmeasured"));

        var only = Assert.Single(await LoadedAsync(svc));

        Assert.Null(only.UploadMbps);
        Assert.Null(only.PingMs);
        Assert.Equal(123.4, only.DownloadMbps, precision: 3);
        Assert.Equal("unmeasured", only.Server);
    }

    [Fact]
    public async Task SaveAsync_LeavesAnUnmeasuredUploadAndPingOutOfTheFile()
    {
        // A version from before they could be missing reads both as plain numbers. A null would make the whole file
        // unreadable to it, where a missing value reads as 0, as it always did.
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Result(up: null, ping: null)));

        var json = await File.ReadAllTextAsync(HistoryFile);

        Assert.Contains("downloadMbps", json);   // the file does hold the entry
        Assert.DoesNotContain("uploadMbps", json);
        Assert.DoesNotContain("pingMs", json);
    }

    [Fact]
    public async Task LoadAsync_APingSavedAsZeroByAnOlderVersion_IsKeptAsSaved()
    {
        // Old results are not reinterpreted. A 0 ms ping from before is usually a ping that got no answer, but a
        // round trip under a millisecond is also 0, and the file cannot tell the two apart.
        await File.WriteAllTextAsync(HistoryFile,
            """[{"engine":"HTTP","downloadMbps":10,"uploadMbps":0,"pingMs":0,"server":"s","completedAt":"2026-01-01T00:00:00"}]""");
        using var svc = NewService();

        var only = Assert.Single(await LoadedAsync(svc));

        Assert.Equal(0, only.PingMs);
        Assert.Equal(0, only.UploadMbps);
    }

    [Fact]
    public async Task ClearAsync_OneEngine_LeavesTheOtherEnginesResults()
    {
        // The discriminating case the old test could not express: clearing HTTP must not take Ookla
        // with it. Previously this only asserted "did not throw" — while deleting the user's history.
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP", 100, 10, 5, "http-server"));
        await svc.SaveAsync(Result("Ookla", 200, 20, 6, "ookla-server"));
        Assert.Equal(2, (await LoadedAsync(svc)).Count);

        Assert.True(await svc.ClearAsync("HTTP"),
            "ClearAsync reported failure — the view model turns that into \"history could not be cleared\" and leaves the rows on screen.");

        var remaining = await LoadedAsync(svc);
        var only = Assert.Single(remaining);
        Assert.Equal("Ookla", only.Engine);
        Assert.Equal("ookla-server", only.Server);
    }

    [Fact]
    public async Task ClearAsync_OneEngine_IsCaseInsensitive()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP", server: "http-server"));

        Assert.True(await svc.ClearAsync("http"), "ClearAsync reported failure.");   // lower case — the service compares OrdinalIgnoreCase

        Assert.Empty(await LoadedAsync(svc));
    }

    [Fact]
    public async Task ClearAsync_LastRemainingEngine_RemovesTheFileEntirely()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP"));
        Assert.True(File.Exists(HistoryFile));

        Assert.True(await svc.ClearAsync("HTTP"), "ClearAsync reported failure.");

        Assert.False(File.Exists(HistoryFile));   // no empty-array file left behind
        Assert.Empty(await LoadedAsync(svc));
    }

    [Fact]
    public async Task ClearAsync_AllEngines_RemovesEverything()
    {
        using var svc = NewService();
        await svc.SaveAsync(Result("HTTP"));
        await svc.SaveAsync(Result("Ookla"));

        Assert.True(await svc.ClearAsync(null), "ClearAsync reported failure.");

        Assert.False(File.Exists(HistoryFile));
        Assert.Empty(await LoadedAsync(svc));
    }

    [Fact]
    public async Task ClearAsync_WhenNoFileExists_DoesNotThrow()
    {
        using var svc = NewService();
        Assert.False(File.Exists(HistoryFile));

        var cleared = false;
        var ex = await Record.ExceptionAsync(async () => cleared = await svc.ClearAsync("HTTP"));

        Assert.Null(ex);
        // Nothing to delete is SUCCESS. Returning false here would surface "history could not be
        // cleared" for a no-op, which is the failure mode the bool was added to prevent.
        Assert.True(cleared, "clearing an absent file reported failure, so the user would be told the "
                           + "clear failed when there was simply nothing to clear.");
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

        var loaded = await LoadedAsync(svc);

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
        {
            // Checked per save, like the sibling above. Without this the test is blind to a dropped
            // write: 21 saves also trim to 20, so both counts still match and only the bool differs.
            var saved = await svc.SaveAsync(Result("HTTP", server: $"h{i}", at: start.AddMinutes(i)));
            Assert.True(saved, $"HTTP save {i} (h{i}) failed to reach disk.");
        }
        Assert.True(await svc.SaveAsync(Result("Ookla", server: "ookla-kept", at: start.AddMinutes(100))),
            "the Ookla save failed to reach disk.");

        var loaded = await LoadedAsync(svc);

        // The exact surviving HTTP set, newest first: h21 down to h2. Counts alone do not discriminate —
        // any 20 of the 22 would satisfy them, including a window that trimmed from the wrong end.
        var expectedHttp = Enumerable.Range(2, 20).Reverse().Select(i => $"h{i}").ToArray();
        Assert.Equal(expectedHttp, loaded.Where(r => r.Engine == "HTTP").Select(r => r.Server).ToArray());
        Assert.Equal(["ookla-kept"], loaded.Where(r => r.Engine == "Ookla").Select(r => r.Server).ToArray());
        Assert.Equal(SpeedTestHistoryService.MaxPerEngine + 1, loaded.Count);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsMalformed_ReturnsEmptyRatherThanThrowing()
    {
        // A truncated or hand-edited file must not take the tab down. This is a real failure path the
        // old tests could not reach, because they never controlled the file's contents.
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        using var svc = NewService();

        var results = await LoadedAsync(svc);

        Assert.Empty(results);
    }

    // ── A history that is there but cannot be read or parsed (#2521) ──────────────────────────────
    //
    // A history that could not be read loaded as empty, so the next save wrote a history holding only the new
    // result over every saved one, and clearing one engine deleted the other engine's results with it. The file is
    // held open with delete sharing only for as long as a read must fail: the read fails, and the replace a write
    // ends with would still succeed, so a writer that refused can be told from one that could not write.

    private FileStream HoldAgainstReads() => new(HistoryFile, FileMode.Open, FileAccess.Read, FileShare.Delete);

    [Fact]
    public async Task LoadAsync_WhenTheFileCannotBeRead_IsNull_NotEmpty()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Result("HTTP", at: new DateTime(2026, 1, 1, 12, 0, 0))));

        using (HoldAgainstReads())
            Assert.Null(await svc.LoadAsync());

        Assert.Single(await LoadedAsync(svc));   // readable again once the file is released
    }

    [Fact]
    public async Task SaveAsync_WhenTheHistoryCannotBeRead_WritesNothing_AndSaysSo()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Result("HTTP", server: "first", at: new DateTime(2026, 1, 1, 12, 0, 0))));
        Assert.True(await svc.SaveAsync(Result("Ookla", server: "second", at: new DateTime(2026, 1, 1, 12, 5, 0))));
        var raised = 0;
        svc.Saved += _ => raised++;

        using (HoldAgainstReads())
            Assert.False(await svc.SaveAsync(Result("HTTP", server: "third", at: new DateTime(2026, 1, 1, 12, 10, 0))));

        Assert.Equal(["second", "first"], (await LoadedAsync(svc)).Select(r => r.Server).ToArray());
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ClearAsync_OneEngine_WhenTheHistoryCannotBeRead_LeavesItAsItIs()
    {
        using var svc = NewService();
        Assert.True(await svc.SaveAsync(Result("HTTP", server: "http-server", at: new DateTime(2026, 1, 1, 12, 0, 0))));
        Assert.True(await svc.SaveAsync(Result("Ookla", server: "ookla-server", at: new DateTime(2026, 1, 1, 12, 5, 0))));

        using (HoldAgainstReads())
            Assert.False(await svc.ClearAsync("HTTP"));

        Assert.Equal(2, (await LoadedAsync(svc)).Count);
    }

    [Fact]
    public async Task SaveAsync_OverAHistoryThatDoesNotParse_KeepsItAside_AndSaves()
    {
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        using var svc = NewService();
        var result = Result("HTTP");

        Assert.True(await svc.SaveAsync(result));

        Assert.Equal("{ this is not valid json", await File.ReadAllTextAsync(HistoryFile + ".unreadable"));
        Assert.Equal(result, Assert.Single(await LoadedAsync(svc)));
    }

    [Fact]
    public async Task SaveAsync_WhenAHistoryThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(HistoryFile + ".unreadable");
        using var svc = NewService();

        Assert.False(await svc.SaveAsync(Result("HTTP")));

        Assert.Equal("{ this is not valid json", await File.ReadAllTextAsync(HistoryFile));
    }

    [Fact]
    public async Task ClearAsync_OneEngine_OverAHistoryThatDoesNotParse_KeepsItAside()
    {
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        using var svc = NewService();

        Assert.True(await svc.ClearAsync("HTTP"));

        Assert.Equal("{ this is not valid json", await File.ReadAllTextAsync(HistoryFile + ".unreadable"));
        Assert.False(File.Exists(HistoryFile));
    }

    [Fact]
    public async Task ClearAsync_OneEngine_WhenAHistoryThatDoesNotParseCannotBeSetAside_LeavesIt()
    {
        await File.WriteAllTextAsync(HistoryFile, "{ this is not valid json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(HistoryFile + ".unreadable");
        using var svc = NewService();

        Assert.False(await svc.ClearAsync("HTTP"));

        Assert.Equal("{ this is not valid json", await File.ReadAllTextAsync(HistoryFile));
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsJsonNull_ReturnsEmpty()
    {
        // Deserialize returns null for the literal "null"; the service guards that explicitly.
        await File.WriteAllTextAsync(HistoryFile, "null");
        using var svc = NewService();

        Assert.Empty(await LoadedAsync(svc));
    }

    [Fact]
    public async Task LoadAsync_MissingEngineField_DefaultsToHttp()
    {
        // The DTO's Engine is nullable and the loader coalesces to "HTTP"; pin that so the fallback
        // cannot silently change and mis-bucket old entries.
        await File.WriteAllTextAsync(HistoryFile,
            """[{"downloadMbps":10,"uploadMbps":2,"pingMs":5,"server":"s","completedAt":"2026-01-01T00:00:00"}]""");
        using var svc = NewService();

        var only = Assert.Single(await LoadedAsync(svc));

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

            Assert.Single(await LoadedAsync(a));
            Assert.Empty(await LoadedAsync(b));
        }
        finally
        {
            Directory.Delete(otherDir, recursive: true);
        }
    }

    // ---------- Saved: one history, two writers (the Speed Test tab and the Dashboard's quick test) ----------

    [Fact]
    public async Task SaveAsync_RaisesSaved_OnceTheResultIsOnDisk()
    {
        using var svc = NewService();
        var result = Result(server: "raised-after-write");
        SpeedTestResult? raised = null;
        var onDiskWhenRaised = false;
        svc.Saved += r =>
        {
            raised = r;
            onDiskWhenRaised = File.Exists(HistoryFile) && File.ReadAllText(HistoryFile).Contains("raised-after-write");
        };

        Assert.True(await svc.SaveAsync(result));

        Assert.Equal(result, raised);
        Assert.True(onDiskWhenRaised, "Saved was raised before the result reached the file.");
    }

    [Fact]
    public async Task SaveAsync_ThatCannotWrite_DoesNotRaiseSaved()
    {
        // A folder where the file goes, so the write fails. A subscriber must not be told about a result the
        // history does not hold.
        Directory.CreateDirectory(HistoryFile);
        using var svc = NewService();
        var raised = false;
        svc.Saved += _ => raised = true;

        Assert.False(await svc.SaveAsync(Result()));
        Assert.False(raised);
    }
}
