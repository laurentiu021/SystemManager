// SysManager · SpeedTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedTestService"/>: the Zip Slip containment check on the Ookla CLI extraction, how the
/// CLI is downloaded, checked against its pinned release and held while it runs, and the ping and upload readings.
/// </summary>
public class SpeedTestServiceTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "smtest_tools");

    [Fact]
    public void IsInsideDirectory_NormalEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "speedtest.exe"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_NestedEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "sub", "license.txt"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_TraversalEntry_IsRejected()
    {
        // A "../" entry that resolves to the parent directory must be rejected.
        var dest = Path.GetFullPath(Path.Join(Root, "..", "evil.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_SiblingWithSharedPrefix_IsRejected()
    {
        // Regression: a sibling directory whose name merely starts with the target's
        // name (e.g. "smtest_tools-evil") must NOT pass the containment check. A naive
        // StartsWith(fullToolsDir) without a trailing separator would wrongly accept it.
        var dest = Path.GetFullPath(Path.Combine(Root + "-evil", "payload.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public async Task RunOoklaAsync_UserCancelDuringPrepare_SurfacesAsCancellation()
    {
        // Regression: the prepare phase (EnsureOoklaAsync) was wrapped in a blanket
        // catch (Exception) that re-threw OperationCanceledException as
        // InvalidOperationException("Could not prepare Ookla CLI: A task was canceled."),
        // bypassing the ViewModel's dedicated "Cancelled" handler and misreporting a
        // clean user cancel as an error. A pre-cancelled token makes the first
        // Task.Run(..., ct) inside EnsureOoklaAsync throw before any network or
        // filesystem work, so this test is deterministic and offline. Not elevated, or the run would be refused
        // before it got that far, as it is on an elevated host.
        using var http = new HttpClient(new Serving([]));
        var svc = new SpeedTestService(http, toolsDir: null, SpeedTestService.OoklaRelease.Pinned, isElevated: () => false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunOoklaAsync(progress: null, cts.Token));
    }    // ---------- the Ookla CLI pin: verified object == executed object ----------

    [Fact]
    public void Pin_WhileHeld_RefusesOverwriteAndRename()
    {
        // The whole point of the pin. The CLI lives in a user-writable directory, so without this a path
        // verified a moment ago can name different bytes by the time Process.Start reads it — and when
        // SysManager is elevated, those bytes run at high integrity.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_pin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pinned.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        try
        {
            using var pin = SpeedTestService.Pin(file);

            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(file, [9, 9, 9, 9]));
            Assert.ThrowsAny<IOException>(() => File.Move(file, Path.Combine(dir, "swapped.bin")));
            Assert.ThrowsAny<IOException>(() => File.Delete(file));

            // And the original bytes are intact, not merely un-renamed.
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public void Pin_WhileHeld_StillAllowsReading()
    {
        // The opposite failure, and the reason this is FileShare.Read rather than FileShare.None: a pin
        // that denied reading would stop the Windows loader mapping the image, so the speed test could
        // never run. Reading is what the loader needs, so reading must stay possible.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_pinread_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pinned.bin");
        File.WriteAllBytes(file, [7, 7, 7]);

        try
        {
            using var pin = SpeedTestService.Pin(file);

            using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(3, reader.Length);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    // ---------- the pinned Ookla release (#2701) ----------
    // Ookla's 1.2.0 CLI carries no signature, so the signature check rejected and deleted the real file on every run,
    // and the Ookla test never ran. It is checked against the SHA-256 of the pinned release instead.

    /// <summary>A stand-in for speedtest.exe: "MZ" and padding, unsigned, as the real one is, and over 1 KB.</summary>
    private static byte[] FakeCli(byte fill = 0x41)
    {
        var bytes = new byte[4096];
        Array.Fill(bytes, fill);
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        return bytes;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>A zip shaped like Ookla's: the exe and its readme, dated so the same input gives the same bytes.</summary>
    private static byte[] ZipOf(byte[] exe)
    {
        var dated = new DateTimeOffset(2022, 9, 1, 0, 0, 0, TimeSpan.Zero);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var cli = zip.CreateEntry("speedtest.exe");
            cli.LastWriteTime = dated;
            using (var body = cli.Open()) body.Write(exe);
            var readme = zip.CreateEntry("speedtest.md");
            readme.LastWriteTime = dated;
            using (var text = new StreamWriter(readme.Open())) text.Write("# Speedtest CLI");
        }
        return buffer.ToArray();
    }

    /// <summary>Answers every request with <paramref name="body"/>, counting the requests.</summary>
    private sealed class Serving(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            });
        }
    }

    private static string NewToolsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_ookla_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PinAndVerify_AnUnsignedFileWithThePinnedHash_IsAccepted_AndHeld()
    {
        var dir = NewToolsDir();
        var file = Path.Combine(dir, "speedtest.exe");
        var cli = FakeCli();
        File.WriteAllBytes(file, cli);

        try
        {
            using (var pinned = SpeedTestService.PinAndVerify(file, Sha256(cli)))
            {
                Assert.Equal(file, pinned.Path);
                Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(file, FakeCli(0x42)));
            }
            Assert.Equal(cli, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PinAndVerify_OneChangedByte_IsRejected_AndTheFileDeleted()
    {
        // The delete happens after the pin is released: the pin denies deletion, and the delete swallows that
        // failure, so in the other order the rejected file would stay in the cache while the message said it was gone.
        var dir = NewToolsDir();
        var file = Path.Combine(dir, "speedtest.exe");
        var cli = FakeCli();
        var pinned = Sha256(cli);
        cli[2048] ^= 0x01;
        File.WriteAllBytes(file, cli);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SpeedTestService.PinAndVerify(file, pinned));
            Assert.Equal("The saved Ookla program was not the release SysManager checks for, so it was deleted. Run the "
                + "test again to download it again.", ex.Message);
            Assert.False(File.Exists(file), "the rejected file is still on disk");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AMissingCli_IsDownloaded_Checked_AndTheZipRemoved()
    {
        // The run that downloaded the CLI used to fail on deleting the zip, which the extraction still held open.
        var dir = NewToolsDir();
        var cli = FakeCli();
        var zip = ZipOf(cli);
        var server = new Serving(zip);
        using var http = new HttpClient(server);
        var release = new SpeedTestService.OoklaRelease("https://ookla.test/cli.zip", Sha256(zip), Sha256(cli));
        var svc = new SpeedTestService(http, dir, release, isElevated: () => false);

        try
        {
            using (var pinned = await svc.EnsureOoklaAsync(progress: null, CancellationToken.None))
                Assert.Equal(Path.Combine(dir, "speedtest.exe"), pinned.Path);

            Assert.Equal(1, server.Requests);
            Assert.Equal(cli, File.ReadAllBytes(Path.Combine(dir, "speedtest.exe")));
            Assert.False(File.Exists(Path.Combine(dir, "ookla.zip")), "the downloaded zip was left behind");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ADownloadThatIsNotThePinnedZip_ExtractsNothing()
    {
        var dir = NewToolsDir();
        var cli = FakeCli();
        var zip = ZipOf(cli);
        using var http = new HttpClient(new Serving(zip));
        var release = new SpeedTestService.OoklaRelease("https://ookla.test/cli.zip", Sha256(ZipOf(FakeCli(0x42))), Sha256(cli));
        var svc = new SpeedTestService(http, dir, release, isElevated: () => false);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.EnsureOoklaAsync(progress: null, CancellationToken.None));
            Assert.Equal("The Ookla download was not the release SysManager checks for, so it was not used.", ex.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ACachedCli_IsCheckedOnEveryRun_AndUsedWithoutADownload()
    {
        var dir = NewToolsDir();
        var cli = FakeCli();
        File.WriteAllBytes(Path.Combine(dir, "speedtest.exe"), cli);
        var server = new Serving([]);
        using var http = new HttpClient(server);
        var svc = new SpeedTestService(http, dir, new("https://ookla.test/cli.zip", "", Sha256(cli)), isElevated: () => false);

        try
        {
            using (await svc.EnsureOoklaAsync(progress: null, CancellationToken.None)) { }
            Assert.Equal(0, server.Requests);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ACachedCliWithAChangedByte_IsDeleted_SoTheNextRunDownloadsItAgain()
    {
        var dir = NewToolsDir();
        var cli = FakeCli();
        var exe = Path.Combine(dir, "speedtest.exe");
        File.WriteAllBytes(exe, FakeCli(0x42));
        var zip = ZipOf(cli);
        var server = new Serving(zip);
        using var http = new HttpClient(server);
        var svc = new SpeedTestService(http, dir, new("https://ookla.test/cli.zip", Sha256(zip), Sha256(cli)), isElevated: () => false);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EnsureOoklaAsync(progress: null, CancellationToken.None));
            Assert.False(File.Exists(exe), "the changed CLI is still cached");
            Assert.Equal(0, server.Requests);

            using (await svc.EnsureOoklaAsync(progress: null, CancellationToken.None)) { }
            Assert.Equal(1, server.Requests);
            Assert.Equal(cli, File.ReadAllBytes(exe));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task WhileElevated_TheOoklaRun_IsRefused_BeforeAnythingIsDownloaded()
    {
        var dir = NewToolsDir();
        var server = new Serving(ZipOf(FakeCli()));
        using var http = new HttpClient(server);
        var svc = new SpeedTestService(http, dir, SpeedTestService.OoklaRelease.Pinned, isElevated: () => true);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RunOoklaAsync(progress: null, CancellationToken.None));
            Assert.Equal(SpeedTestService.OoklaNeedsStandardRunMessage, ex.Message);
            Assert.Equal(0, server.Requests);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ThePinnedRelease_Is_Ooklas64BitWindowsBuild_WithBothHashesWhole()
    {
        var pinned = SpeedTestService.OoklaRelease.Pinned;

        Assert.Equal("https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip", pinned.ZipUrl);
        Assert.Matches("^[0-9A-F]{64}$", pinned.ZipSha256);
        Assert.Matches("^[0-9A-F]{64}$", pinned.ExeSha256);
        Assert.NotEqual(pinned.ZipSha256, pinned.ExeSha256);
    }

    // ---------- a ping or upload that was not measured is not a number (#2504) ----------
    //
    // Both used to be 0. 0 ms is a perfect ping, so a network that drops ping was shown the best reading there is,
    // and 0 Mbps reads as a dead line rather than as no reading.

    /// <summary>Answers each of the four pings in turn.</summary>
    private static Func<CancellationToken, Task<(IPStatus Status, long RoundtripTime)>> Answering(
        params (IPStatus Status, long RoundtripTime)[] replies)
    {
        var next = 0;
        return _ => Task.FromResult(replies[next++]);
    }

    [Fact]
    public async Task MeasurePing_NoPingAnswered_IsNotMeasured()
    {
        var ping = await SpeedTestService.MeasurePingAsync(
            Answering((IPStatus.TimedOut, 0), (IPStatus.TimedOut, 0), (IPStatus.DestinationHostUnreachable, 0), (IPStatus.TimedOut, 0)),
            CancellationToken.None);

        Assert.Null(ping);
    }

    [Fact]
    public async Task MeasurePing_AveragesOnlyThePingsThatWereAnswered()
    {
        var ping = await SpeedTestService.MeasurePingAsync(
            Answering((IPStatus.Success, 10), (IPStatus.TimedOut, 0), (IPStatus.Success, 20), (IPStatus.TimedOut, 0)),
            CancellationToken.None);

        Assert.Equal(15.0, ping);
    }

    [Fact]
    public async Task MeasurePing_AnswersUnderAMillisecond_AreStillAMeasurement()
    {
        // Windows reports a round trip under a millisecond as 0. That is a reading, and must not be taken for none.
        var ping = await SpeedTestService.MeasurePingAsync(
            Answering((IPStatus.Success, 0), (IPStatus.Success, 0), (IPStatus.Success, 0), (IPStatus.Success, 0)),
            CancellationToken.None);

        Assert.Equal(0.0, ping);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("socket")]
    [InlineData("busy")]
    public async Task MeasurePing_APingThatCannotBeSent_IsNotMeasured(string failure)
    {
        Exception thrown = failure switch
        {
            "unresolved" => new PingException("An exception occurred during a Ping request."),
            "socket" => new System.Net.Sockets.SocketException(),
            _ => new InvalidOperationException("An asynchronous call is already in progress."),
        };

        var ping = await SpeedTestService.MeasurePingAsync(_ => throw thrown, CancellationToken.None);

        Assert.Null(ping);
    }

    [Fact]
    public async Task MeasurePing_Cancelled_IsACancel_NotAPingWithNoAnswer()
    {
        // A cancelled test reaches the view model as "Cancelled". Turned into a missing ping, the run would carry on
        // into the download.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTestService.MeasurePingAsync(
            token => Task.FromCanceled<(IPStatus, long)>(token), cts.Token));
    }

    [Fact]
    public void Upload_TheServerRefused_IsNotMeasured()
        => Assert.Null(SpeedTestService.UploadMbps(accepted: false, sentBytes: 52_428_800, TimeSpan.FromSeconds(4)));

    [Fact]
    public void Upload_Accepted_IsTheBytesSentOverTheTimeTaken()
    {
        // 50 MiB in 4 s: 52,428,800 bytes × 8 / 1,000,000 / 4.
        var mbps = SpeedTestService.UploadMbps(accepted: true, sentBytes: 52_428_800, TimeSpan.FromSeconds(4));

        Assert.NotNull(mbps);
        Assert.Equal(104.8576, mbps.Value, precision: 6);
    }

    // ── The upload's window (#2721) ──
    // The upload waited for the whole body through a client that gives up at two minutes, so on a slow connection
    // it was cancelled, and the whole test with it: the ping and download already measured were lost.

    /// <summary>The upload's clock: its first reading is the start, and every later one ten seconds on.</summary>
    private sealed class TenSecondUpload : TimeProvider
    {
        private int _readings;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _readings++ == 0 ? 0 : TimeSpan.FromSeconds(10).Ticks;
    }

    /// <summary>A server that takes <paramref name="taking"/> bytes of the upload, then answers as
    /// <paramref name="then"/> does.</summary>
    private sealed class UploadServer(long taking, Func<CancellationToken, Task<HttpResponseMessage>> then)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            long taken = 0;
            while (taken < taking)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, taking - taken)), ct);
                if (read == 0) break;
                taken += read;
            }
            return await then(ct);
        }
    }

    /// <summary>What the server does once it has taken its part: the window ends while the upload is still going.</summary>
    private static Func<CancellationToken, Task<HttpResponseMessage>> EndingTheWindow(CancellationTokenSource window) =>
        async ct =>
        {
            await window.CancelAsync();
            await new TaskCompletionSource().Task.WaitAsync(ct);   // until the upload is cancelled
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

    [Fact]
    public async Task Upload_ThatTheWindowEnds_IsMeasuredOnWhatItHadSent()
    {
        using var window = new CancellationTokenSource();
        using var http = new HttpClient(new UploadServer(1024 * 1024, EndingTheWindow(window)));

        var mbps = await SpeedTestService.MeasureUploadAsync(http, window.Token, new TenSecondUpload(), null,
                                                             CancellationToken.None);

        // 1 MiB in 10 s: 1,048,576 bytes × 8 / 1,000,000 / 10.
        Assert.NotNull(mbps);
        Assert.Equal(0.8388608, mbps.Value, precision: 6);
    }

    [Fact]
    public async Task Upload_ThatTheWindowEndsBeforeAnythingWasSent_IsNotMeasured()
    {
        using var window = new CancellationTokenSource();
        using var http = new HttpClient(new UploadServer(0, EndingTheWindow(window)));

        Assert.Null(await SpeedTestService.MeasureUploadAsync(http, window.Token, new TenSecondUpload(), null,
                                                              CancellationToken.None));
    }

    [Fact]
    public async Task Upload_TheUserCancelling_StillCancelsTheTest()
    {
        using var user = new CancellationTokenSource();
        using var http = new HttpClient(new UploadServer(64 * 1024, async ct =>
        {
            await user.CancelAsync();
            await new TaskCompletionSource().Task.WaitAsync(ct);   // until the upload is cancelled
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTestService.MeasureUploadAsync(
            http, CancellationToken.None, new TenSecondUpload(), null, user.Token));
    }

    [Fact]
    public async Task Upload_ThatFinishesInTheWindow_IsMeasuredOnTheWholeBody()
    {
        using var http = new HttpClient(new UploadServer(long.MaxValue,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var mbps = await SpeedTestService.MeasureUploadAsync(http, CancellationToken.None, new TenSecondUpload(), null,
                                                             CancellationToken.None);

        // The whole 50 MiB in 10 s.
        Assert.NotNull(mbps);
        Assert.Equal(41.94304, mbps.Value, precision: 6);
    }
}
