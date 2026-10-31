// SysManager · SpeedTestService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Speed test with two engines:
///  - HTTP: downloads/uploads known-size payloads from speed.cloudflare.com.
///    Zero dependencies, no admin, runs everywhere.
///  - Ookla: downloads the official speedtest.exe CLI on first use into
///    %LOCALAPPDATA%\SysManager\tools, checks it against the pinned release on every run, then runs it with
///    --format=json. It does not run while SysManager is elevated.
/// Progress reporting is in percent (0-100) plus a free-form status message.
/// </summary>
public sealed class SpeedTestService : ISpeedTestService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly HttpClient _download;
    private readonly string? _toolsDir;
    private readonly OoklaRelease _ookla;
    private readonly Func<bool> _isElevated;

    public SpeedTestService() : this(_http, toolsDir: null, OoklaRelease.Pinned, AdminHelper.IsElevated) { }

    /// <summary>
    /// Prepares the Ookla CLI through <paramref name="download"/> into <paramref name="toolsDir"/> and checks it
    /// against <paramref name="ookla"/>, so a test needs no network and no real release.
    /// </summary>
    /// <param name="toolsDir">Null for <see cref="AppDataFolders.Tools"/>, read on each run.</param>
    internal SpeedTestService(HttpClient download, string? toolsDir, OoklaRelease ookla, Func<bool> isElevated)
    {
        _download = download;
        _toolsDir = toolsDir;
        _ookla = ookla;
        _isElevated = isElevated;
    }

    // Cloudflare returns exactly N bytes from these endpoints; perfect for timing.
    private const string CfDownloadUrl = "https://speed.cloudflare.com/__down?bytes={0}";
    private const string CfUploadUrl = "https://speed.cloudflare.com/__up";
    private const string CfPingHost = "speed.cloudflare.com";

    // 50 MB with 8 parallel streams saturates high-speed links (1 Gbps+).
    private const long PayloadBytes = 50L * 1024 * 1024;

    /// <summary>The longest the upload is measured for.</summary>
    /// <remarks>
    /// The upload waited for the whole 50 MB body through the shared client, whose timeout is two minutes, so an
    /// upload slower than about 3.5 Mbps, which ADSL and weak mobile connections often are, was cancelled at two
    /// minutes. That cancellation ended the whole test: the ping and download already measured were lost, nothing was
    /// saved, and the tab said "Cancelled" (#2721). An upload still running when this window ends is measured on what
    /// it had sent by then.
    /// </remarks>
    internal static readonly TimeSpan UploadWindow = TimeSpan.FromSeconds(30);
    private const int DownloadConnections = 8; // parallel streams for accurate throughput

    public async Task<SpeedTestResult> RunHttpAsync(
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        progress?.Report((0, "Pinging test server…"));
        var pingMs = await MeasurePingAsync(CfPingHost, ct).ConfigureAwait(false);

        progress?.Report((5, "Measuring download…"));
        var downloadMbps = await MeasureDownloadAsync(progress, ct).ConfigureAwait(false);

        progress?.Report((55, "Measuring upload…"));
        var uploadMbps = await MeasureUploadAsync(progress, ct).ConfigureAwait(false);

        progress?.Report((100, "Done"));
        return new SpeedTestResult("HTTP", downloadMbps, uploadMbps, pingMs, CfPingHost, DateTime.Now);
    }

    private static async Task<double?> MeasurePingAsync(string host, CancellationToken ct)
    {
        using var p = new Ping();
        return await MeasurePingAsync(async token =>
        {
            // Pass the token so cancelling the speed test interrupts the ping phase
            // immediately, instead of running all four 2 s probes (up to 8 s) first.
            var r = await p.SendPingAsync(host, TimeSpan.FromMilliseconds(2000), cancellationToken: token).ConfigureAwait(false);
            return (r.Status, r.RoundtripTime);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The average round trip of four pings, or null when none got an answer.
    /// </summary>
    /// <remarks>
    /// Null, not 0, for a ping that was never answered, whether the host timed out, the network drops ping or
    /// the ping could not be sent. 0 ms is a perfect ping, and networks that drop ping are common, so they were
    /// shown the best reading there is (#2504). A reply of 0 ms is still a measurement: Windows reports a
    /// round trip under a millisecond as 0.
    /// <para>The body is handed the single ping, so a test can answer, time out or throw without a network.</para>
    /// </remarks>
    internal static async Task<double?> MeasurePingAsync(
        Func<CancellationToken, Task<(IPStatus Status, long RoundtripTime)>> sendPing, CancellationToken ct)
    {
        try
        {
            List<long> samples = [];
            for (int i = 0; i < 4; i++)
            {
                var (status, roundtripTime) = await sendPing(ct).ConfigureAwait(false);
                if (status == IPStatus.Success) samples.Add(roundtripTime);
            }
            return samples.Count > 0 ? samples.Average() : null;
        }
        catch (System.Net.NetworkInformation.PingException) { return null; }
        catch (System.Net.Sockets.SocketException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<double> MeasureDownloadAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        // Use multiple parallel connections to saturate the link, similar to
        // how Ookla and fast.com measure throughput (#152).
        var perStream = PayloadBytes / DownloadConnections;
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, DownloadConnections).Select(async _ =>
        {
            var url = string.Format(CfDownloadUrl, perStream);
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var buffer = new byte[81920];
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                Interlocked.Add(ref totalBytes, read);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        var downloaded = Interlocked.Read(ref totalBytes);
        progress?.Report((50, $"Download: {downloaded / 1024 / 1024} MB"));
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return downloaded * 8.0 / 1_000_000.0 / seconds;
    }

    private static async Task<double?> MeasureUploadAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        using var window = new CancellationTokenSource(UploadWindow);
        return await MeasureUploadAsync(_http, window.Token, TimeProvider.System, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The upload through <paramref name="http"/>, measured until it ends or <paramref name="windowEnd"/> does, or
    /// null when it could not be measured.
    /// </summary>
    /// <param name="http">The client the upload goes through.</param>
    /// <param name="windowEnd">Ends the upload's window, <see cref="UploadWindow"/> after it starts.</param>
    /// <param name="time">Times the upload.</param>
    /// <param name="progress">Told how the upload went.</param>
    /// <param name="ct">The user's cancellation, which still cancels the test.</param>
    internal static async Task<double?> MeasureUploadAsync(HttpClient http, CancellationToken windowEnd, TimeProvider time,
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        // Stream random data in chunks instead of allocating a single 50 MB
        // array on the Large Object Heap. Each chunk is small enough to stay
        // in Gen0 and be collected quickly.
        const int ChunkSize = 256 * 1024; // 256 KB per chunk

        var stream = new RandomChunkStream(PayloadBytes, ChunkSize);
        using var content = new StreamContent(stream, ChunkSize);
        content.Headers.ContentLength = PayloadBytes;
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct, windowEnd);

        var started = time.GetTimestamp();
        try
        {
            using var resp = await http.PostAsync(CfUploadUrl, content, upload.Token).ConfigureAwait(false);
            var elapsed = time.GetElapsedTime(started);

            var sentBytes = stream.Position;
            var mbps = UploadMbps(resp.IsSuccessStatusCode, sentBytes, elapsed);
            progress?.Report((95, mbps is null
                ? $"Upload measurement failed (HTTP {(int)resp.StatusCode})"
                : $"Upload complete: {sentBytes / 1024 / 1024} MB"));
            return mbps;
        }
        // The window ended it, not the user: measured on what had been sent by then.
        catch (OperationCanceledException) when (windowEnd.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var sentBytes = stream.Position;
            var mbps = WindowedUploadMbps(sentBytes, time.GetElapsedTime(started));
            progress?.Report((95, mbps is null
                ? "Upload measurement failed: nothing was sent in time"
                : $"Upload: {sentBytes / 1024 / 1024} MB before the time limit"));
            return mbps;
        }
    }

    /// <summary>
    /// The upload rate in Mbps of an upload its window ended, or null when nothing had been sent.
    /// </summary>
    /// <remarks>
    /// On the bytes the HTTP stack had taken from the body, which run ahead of the wire by what the connection's send
    /// buffer holds: a few percent over the window, on a connection slow enough to reach its end.
    /// </remarks>
    internal static double? WindowedUploadMbps(long sentBytes, TimeSpan elapsed)
    {
        if (sentBytes <= 0) return null;
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return sentBytes * 8.0 / 1_000_000.0 / seconds;
    }

    /// <summary>
    /// The upload rate in Mbps, or null when the server did not accept the upload.
    /// </summary>
    /// <remarks>
    /// If the server rejects the POST (e.g. 4xx on size) it can return before the full payload is sent.
    /// Reporting the payload over the now-tiny elapsed time would fabricate a grossly inflated upload speed, so a
    /// rejected upload is not measured. It used to be reported as 0 Mbps, a reading of a dead line rather than
    /// no reading (#2504).
    /// <para>Measured on the bytes actually consumed by the HTTP stack (the stream's final position), not the
    /// intended payload, so a short-circuited upload reports the true transferred amount instead of the full
    /// 50 MB.</para>
    /// </remarks>
    internal static double? UploadMbps(bool accepted, long sentBytes, TimeSpan elapsed)
    {
        if (!accepted) return null;
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return sentBytes * 8.0 / 1_000_000.0 / seconds;
    }

    /// <summary>
    /// A read-only stream that produces random bytes in fixed-size chunks
    /// without allocating the entire payload up front.
    /// </summary>
    private sealed class RandomChunkStream : Stream
    {
        private readonly long _length;
        private readonly byte[] _chunk;
        private long _position;

        public RandomChunkStream(long length, int chunkSize)
        {
            _length = length;
            _chunk = new byte[chunkSize];
            Random.Shared.NextBytes(_chunk);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toRead = (int)Math.Min(count, _length - _position);
            if (toRead <= 0) return 0;

            var written = 0;
            while (written < toRead)
            {
                var batch = Math.Min(toRead - written, _chunk.Length);
                Buffer.BlockCopy(_chunk, 0, buffer, offset + written, batch);
                written += batch;
            }
            _position += written;
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------- Ookla ----------------

    /// <summary>
    /// The one Ookla CLI build SysManager runs: where its zip is downloaded from, and the SHA-256 of the zip and of
    /// the <c>speedtest.exe</c> in it.
    /// </summary>
    /// <remarks>
    /// The build carries no signature, so these hashes are the whole check, and a changed byte in either file fails
    /// it. The signature check they replace rejected the real file on every run, and could not have caught a changed
    /// one either: <c>X509Certificate.CreateFromSignedFile</c> reads the signer certificate without checking the
    /// file's bytes against the signature (#2701). Moving to a newer CLI means changing all three values together.
    /// </remarks>
    internal sealed record OoklaRelease(string ZipUrl, string ZipSha256, string ExeSha256)
    {
        /// <summary>
        /// Ookla CLI 1.2.0 for 64-bit Windows. SysManager ships only as an x64 build, and Ookla publishes no 32-bit
        /// Windows build of 1.2.0: that URL answers 403.
        /// </summary>
        internal static OoklaRelease Pinned { get; } = new(
            "https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip",
            "13E3D888B845D301A556419E31F14AB9BFF57E3F06089EF2FD3BDC9BA6841EFA",
            "C73C4AC2A7CDC48829FE0F9D7EC91CA41C8B434490D4EB3205AAD69C7AA3B921");
    }

    /// <summary>What the Ookla card says while SysManager is elevated, when the CLI does not run.</summary>
    public const string OoklaNeedsStandardRunMessage =
        "The Ookla test does not run while SysManager is running as administrator, because its program is kept in "
        + "a folder your own account can change. Close SysManager and open it normally, then try again, or use the "
        + "HTTP test.";

    /// <summary>
    /// A verified Ookla CLI, held open so the bytes that were verified are the bytes that get executed.
    /// </summary>
    /// <remarks>
    /// The CLI is cached under <c>%LocalAppData%\SysManager\tools</c>, which the user — and therefore any
    /// process running as the user — can write to. Hashing a path and then launching that same path
    /// leaves a window in which the file can be replaced. So the hash is read through this handle.
    /// <para>Holding the file with <see cref="FileShare.Read"/> denies both writing and deleting for the
    /// lifetime of this object, so the path cannot come to mean a different file. Measured that this does
    /// not prevent execution: a pinned image still loads and runs, while an overwrite and a rename are both
    /// refused. The same "act on the object you validated, not on the string" reasoning is written out at
    /// length in <see cref="FileShredderService"/>.</para>
    /// </remarks>
    internal sealed class PinnedCli(string path, FileStream pin) : IDisposable
    {
        /// <summary>Full path of the pinned executable. Only meaningful while this object is alive.</summary>
        public string Path { get; } = path;

        public void Dispose() => pin.Dispose();
    }

    /// <summary>
    /// Pins <paramref name="exe"/> against modification and deletion, then checks its SHA-256 against
    /// <paramref name="expectedSha256"/>, reading it through that same handle. On success the caller owns the pin and
    /// must keep it alive until the process has exited.
    /// </summary>
    /// <remarks>
    /// Pin first, hash second. The other order would leave the same gap, only narrower.
    /// <para>On failure the pin is released BEFORE the rejected binary is deleted. That order is not
    /// incidental: the pin denies deletion, and <see cref="TryDelete"/> swallows the resulting
    /// IOException at Debug level, so deleting while pinned would silently leave a binary that failed
    /// verification sitting in the cache. Verification decides; this method cleans up.</para>
    /// </remarks>
    internal static PinnedCli PinAndVerify(string exe, string expectedSha256)
    {
        var pin = Pin(exe);
        var verified = false;
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(pin));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Ookla speedtest.exe has SHA256 {Actual}, not the pinned {Expected}", actual, expectedSha256);
                throw new InvalidOperationException(
                    "The saved Ookla program was not the release SysManager checks for, so it was deleted. Run the "
                    + "test again to download it again.");
            }
            verified = true;
            return new PinnedCli(exe, pin);
        }
        finally
        {
            if (!verified)
            {
                pin.Dispose();
                TryDelete(exe);
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="exe"/> for reading with writing and deleting denied to everyone else.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the sharing mode can be unit-tested directly. Getting it wrong in
    /// either direction is silent: too permissive and the swap this exists to prevent is still possible,
    /// too restrictive and the image can no longer be executed at all.
    /// </remarks>
    internal static FileStream Pin(string exe) =>
        new(exe, FileMode.Open, FileAccess.Read, FileShare.Read);

    public async Task<SpeedTestResult> RunOoklaAsync(
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct, int? serverId = null)
    {
        // The CLI is kept in a folder the user's own account can write to and inherits SysManager's token, so elevated
        // it would run, as administrator, a DLL anyone with that account put beside it: Windows looks for a DLL in the
        // exe's own folder first. A speed test needs no rights, so elevated it does not run at all (#2701).
        if (_isElevated()) throw new InvalidOperationException(OoklaNeedsStandardRunMessage);

        // Held for the whole run, not only the preparation: the pin is what makes the verified binary and
        // the executed binary the same object. Scoped by the `using` below, after the process has exited.
        PinnedCli cli;
        try
        {
            cli = await EnsureOoklaAsync(progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User cancel during the first-run download/extract — let it propagate so
            // the ViewModel's dedicated "Cancelled" handler runs instead of misreporting
            // a clean cancel as "Error: Could not prepare Ookla CLI: A task was canceled."
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Cancelled without our token being signalled = HttpClient's own 2-minute
            // timeout fired during the download — a network failure, not a user cancel.
            throw new InvalidOperationException("Could not prepare Ookla CLI: download timed out.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Wrap real prepare failures (HttpRequestException, IOException, InvalidDataException,
            // FileNotFoundException, …) in a friendly message the ViewModel displays. The filter
            // mechanically guarantees cancellation can never be swallowed here even if the
            // OCE branches above are ever restructured.
            throw new InvalidOperationException($"Could not prepare Ookla CLI: {ex.Message}", ex);
        }

        using var pinnedCli = cli;
        var exe = pinnedCli.Path;

        progress?.Report((20, "Running Ookla speedtest…"));
        var args = "--accept-license --accept-gdpr --format=json --progress=no";
        if (serverId is not null)
            args += $" --server-id={serverId}";
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,   // suppress Win32 "DLL not found" system dialogs
            // System32 rather than the tools folder, so nothing is looked up relative to a folder the user can write
            // to. It does not stop a DLL beside the exe from loading, since Windows searches the exe's own folder
            // first: that is why the CLI never runs elevated, and why nothing is extracted from a zip that is not
            // the pinned one.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        // Start the process on a thread-pool thread so Process.Start()
        // never blocks the UI thread. Link a 5-minute timeout to prevent
        // indefinite hangs if the Ookla CLI freezes (CQ-003).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        var linked = timeoutCts.Token;

        using var proc = new Process();
        proc.StartInfo = psi;
        await Task.Run(() => proc.Start(), linked).ConfigureAwait(false);

        // Read stdout and stderr in parallel to prevent pipe buffer deadlock.
        // If one pipe fills while the other is being read sequentially, the
        // child process blocks indefinitely (classic Windows pipe deadlock).
        string stdout, stderr;
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);

            await proc.WaitForExitAsync(linked).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or user cancellation can hit during the pipe reads OR the wait —
            // kill the child either way so speedtest.exe is never orphaned. (Previously
            // the kill only covered WaitForExitAsync, so a cancel during the reads
            // leaked the process.)
            // Kill(entireProcessTree: true) can throw Win32Exception (access denied) or
            // AggregateException (a descendant couldn't be terminated) as well as
            // InvalidOperationException — swallow all three so a failed cancel-kill never
            // masks the OperationCanceledException re-thrown below (same filter as
            // PowerShellRunner's cancel-kill).
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or AggregateException)
            {
                // Gone, denied or half-killed: whichever, the cancellation below is what the caller must see.
            }
            throw;
        }

        // A failed run keeps the exe: its bytes were checked against the pinned release a moment ago, so it is not
        // corrupt, and downloading the same file again cannot change how it runs.
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Ookla failed ({proc.ExitCode}): {stderr}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Ookla returned invalid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            try
            {
                var root = doc.RootElement;

                var downBps = root.GetProperty("download").GetProperty("bandwidth").GetDouble();
                var upBps = root.GetProperty("upload").GetProperty("bandwidth").GetDouble();
                var pingMs = root.GetProperty("ping").GetProperty("latency").GetDouble();
                var server = root.TryGetProperty("server", out var sv)
                    ? $"{sv.GetProperty("name").GetString()} ({sv.GetProperty("location").GetString()})"
                    : "unknown";

                // Ookla reports bandwidth in bytes/sec.
                progress?.Report((100, "Done"));
                return new SpeedTestResult("Ookla",
                    downBps * 8.0 / 1_000_000.0,
                    upBps * 8.0 / 1_000_000.0,
                    pingMs,
                    server,
                    DateTime.Now);
            }
            catch (KeyNotFoundException ex)
            {
                throw new InvalidOperationException($"Ookla JSON missing expected fields: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// The cached CLI, checked and pinned, or else a fresh download of the pinned release. Internal so a test can
    /// prepare the CLI without running it.
    /// </summary>
    internal async Task<PinnedCli> EnsureOoklaAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var toolsDir = _toolsDir ?? AppDataFolders.Tools;

        // Run all synchronous file-system checks on a thread-pool thread
        // so the UI thread is never blocked by disk I/O.
        var needsDownload = await Task.Run(() =>
        {
            Directory.CreateDirectory(toolsDir);
            var path = Path.Join(toolsDir, "speedtest.exe");
            if (File.Exists(path) && new FileInfo(path).Length < 1024)
                TryDelete(path);
            return !File.Exists(path);
        }, ct).ConfigureAwait(false);

        var exe = Path.Join(toolsDir, "speedtest.exe");
        if (!needsDownload)
        {
            // TOCTOU guard: the cached exe lives in a user-writable dir, so it could be swapped
            // between runs. Its hash is checked every time before we hand it back to be executed,
            // not only right after download. The pin travels back with it, so the path cannot
            // change meaning between here and Process.Start.
            return await Task.Run(() => PinAndVerify(exe, _ookla.ExeSha256), ct).ConfigureAwait(false);
        }

        progress?.Report((5, "Downloading Ookla CLI…"));
        var zipPath = Path.Join(toolsDir, "ookla.zip");
        using (var resp = await _download.GetAsync(_ookla.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        // Nothing is extracted from a zip that is not the pinned one, so a changed archive cannot put a file
        // beside the exe, where Windows would load it as one of the exe's own DLLs.
        await Task.Run(() =>
        {
            string actual;
            using (var stream = File.OpenRead(zipPath))
                actual = Convert.ToHexString(SHA256.HashData(stream));
            if (string.Equals(actual, _ookla.ZipSha256, StringComparison.OrdinalIgnoreCase)) return;

            Log.Warning("Ookla CLI download {Url} has SHA256 {Actual}, not the pinned {Expected}",
                _ookla.ZipUrl, actual, _ookla.ZipSha256);
            TryDelete(zipPath);
            throw new InvalidOperationException(
                "The Ookla download was not the release SysManager checks for, so it was not used.");
        }, ct).ConfigureAwait(false);

        progress?.Report((15, "Extracting…"));
        await Task.Run(() =>
        {
            // SEC-M3: Manual extraction with Zip Slip protection.
            // ZipFile.ExtractToDirectory does not validate that entry paths
            // stay within the target directory — a crafted zip with "../"
            // entries could write files outside toolsDir.
            // A block, not a using declaration: the archive holds the zip open without delete sharing, so the
            // delete below would fail while it is still open.
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                {
                    var destinationPath = Path.GetFullPath(Path.Join(toolsDir, entry.FullName));
                    if (!IsInsideDirectory(toolsDir, destinationPath))
                    {
                        Log.Warning("Zip Slip attempt blocked: {Entry} resolves outside target dir", entry.FullName);
                        continue;
                    }

                    // Ensure subdirectory exists
                    var entryDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(entryDir))
                        Directory.CreateDirectory(entryDir);

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }
            // A zip left behind is only a file the next download overwrites, so it does not fail the run.
            TryDelete(zipPath);
        }, ct).ConfigureAwait(false);

        if (!File.Exists(exe))
            throw new FileNotFoundException("speedtest.exe not found after extraction");

        // Checked again after extraction, through the pin, for the same reason as the cached branch above.
        // Extraction has just written this file into a directory the user can write to, so the gap between
        // checking it and executing it is exactly as open on the first run as on every later one.
        return await Task.Run(() => PinAndVerify(exe, _ookla.ExeSha256), ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Log.Debug("Cleanup failed (locked): {Error}", LogService.SanitizePath(ex.Message)); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Cleanup failed (access): {Error}", LogService.SanitizePath(ex.Message)); }
    }

    /// <summary>
    /// True if <paramref name="candidateFullPath"/> resolves to a location strictly
    /// inside <paramref name="directory"/> — the Zip Slip containment check.
    /// The target directory is normalized to end in a separator so a sibling whose
    /// name merely starts with the target's name (e.g. "…\tools-evil" vs "…\tools")
    /// cannot pass a naive prefix test. Internal for unit testing.
    /// </summary>
    internal static bool IsInsideDirectory(string directory, string candidateFullPath)
    {
        var fullDir = Path.GetFullPath(directory);
        var prefix = fullDir.EndsWith(Path.DirectorySeparatorChar)
            ? fullDir
            : fullDir + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
