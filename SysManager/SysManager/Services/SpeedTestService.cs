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
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Speed test with two engines:
///  - HTTP: downloads/uploads known-size payloads from speed.cloudflare.com.
///    Zero dependencies, no admin, runs everywhere.
///  - Ookla: downloads the official speedtest.exe CLI on first use into
///    %LOCALAPPDATA%\SysManager\tools, then runs it with --format=json.
/// Progress reporting is in percent (0-100) plus a free-form status message.
/// </summary>
public sealed class SpeedTestService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Cloudflare returns exactly N bytes from these endpoints; perfect for timing.
    private const string CfDownloadUrl = "https://speed.cloudflare.com/__down?bytes={0}";
    private const string CfUploadUrl = "https://speed.cloudflare.com/__up";
    private const string CfPingHost = "speed.cloudflare.com";

    // 50 MB with 8 parallel streams saturates high-speed links (1 Gbps+).
    private const long PayloadBytes = 50L * 1024 * 1024;
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

    private static async Task<double> MeasurePingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var p = new Ping();
            List<long> samples = [];
            for (int i = 0; i < 4; i++)
            {
                // Pass the token so cancelling the speed test interrupts the ping phase
                // immediately, instead of running all four 2 s probes (up to 8 s) first.
                var r = await p.SendPingAsync(host, TimeSpan.FromMilliseconds(2000), cancellationToken: ct).ConfigureAwait(false);
                if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime);
            }
            return samples.Count > 0 ? samples.Average() : 0;
        }
        catch (System.Net.NetworkInformation.PingException) { return 0; }
        catch (System.Net.Sockets.SocketException) { return 0; }
        catch (InvalidOperationException) { return 0; }
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

    private static async Task<double> MeasureUploadAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        // Stream random data in chunks instead of allocating a single 50 MB
        // array on the Large Object Heap. Each chunk is small enough to stay
        // in Gen0 and be collected quickly.
        const int ChunkSize = 256 * 1024; // 256 KB per chunk

        var stream = new RandomChunkStream(PayloadBytes, ChunkSize);
        using var content = new StreamContent(stream, ChunkSize);
        content.Headers.ContentLength = PayloadBytes;

        var sw = Stopwatch.StartNew();
        using var resp = await _http.PostAsync(CfUploadUrl, content, ct).ConfigureAwait(false);
        sw.Stop();

        // If the server rejects the POST (e.g. 4xx on size) it can return before the
        // full payload is sent. Reporting PayloadBytes over the now-tiny elapsed time
        // would fabricate a grossly inflated upload speed, so treat a non-success
        // response as a failed measurement (0) rather than a real number.
        if (!resp.IsSuccessStatusCode)
        {
            progress?.Report((95, $"Upload measurement failed (HTTP {(int)resp.StatusCode})"));
            return 0;
        }

        // Measure the bytes actually consumed by the HTTP stack (the stream's final
        // position), not the intended payload — so a short-circuited upload reports
        // the true transferred amount instead of the full 50 MB.
        var sentBytes = stream.Position;
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        progress?.Report((95, $"Upload complete: {sentBytes / 1024 / 1024} MB"));
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
    /// A verified Ookla CLI, held open so the bytes that were verified are the bytes that get executed.
    /// </summary>
    /// <remarks>
    /// The CLI is cached under <c>%LocalAppData%\SysManager\tools</c>, which the user — and therefore any
    /// process running as the user — can write to. Verifying a path and then launching that same path
    /// leaves a window in which the file can be replaced, and when SysManager is elevated that window is a
    /// way to get arbitrary code executed at high integrity by a caller who could not reach it otherwise.
    /// Signature verification cannot close it, because the bytes checked are not the bytes mapped.
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
    /// Pins <paramref name="exe"/> against modification and deletion, then verifies it. On success the
    /// caller owns the pin and must keep it alive until the process has exited.
    /// </summary>
    /// <remarks>
    /// Pin first, verify second. The other order would leave the same gap, only narrower.
    /// <para>On failure the pin is released BEFORE the rejected binary is deleted. That order is not
    /// incidental: the pin denies deletion, and <see cref="TryDeleteExe"/> swallows the resulting
    /// IOException at Debug level, so deleting while pinned would silently leave a binary that failed
    /// verification sitting in the cache. Verification decides; this method cleans up.</para>
    /// </remarks>
    internal static PinnedCli PinAndVerify(string exe)
    {
        var pin = Pin(exe);
        var verified = false;
        try
        {
            VerifyOoklaSignature(exe);
            verified = true;
            return new PinnedCli(exe, pin);
        }
        finally
        {
            if (!verified)
            {
                pin.Dispose();
                TryDeleteExe(exe);
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
            // SEC-M4: Set working directory to System32 instead of the tools dir.
            // This prevents DLL hijacking via CWD search order — if an attacker
            // plants a malicious DLL in the user-writable tools directory, the
            // process won't load it because CWD is System32 (admin-protected).
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            // If the exe is broken/corrupt, delete it so next run re-downloads it.
            if (proc.ExitCode == -1073741515) // STATUS_DLL_NOT_FOUND
            {
                try { File.Delete(exe); }
                catch (IOException ex2) { Log.Debug("Cleanup failed (locked): {Error}", LogService.SanitizePath(ex2.Message)); }
                catch (UnauthorizedAccessException ex2) { Log.Debug("Cleanup failed (access): {Error}", LogService.SanitizePath(ex2.Message)); }
            }
            throw new InvalidOperationException($"Ookla failed ({proc.ExitCode}): {stderr}");
        }

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

    private static async Task<PinnedCli> EnsureOoklaAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var toolsDir = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "tools");

        // Run all synchronous file-system checks on a thread-pool thread
        // so the UI thread is never blocked by disk I/O.
        var needsDownload = await Task.Run(() =>
        {
            Directory.CreateDirectory(toolsDir);
            var path = Path.Join(toolsDir, "speedtest.exe");
            if (File.Exists(path) && new FileInfo(path).Length < 1024)
            {
                try { File.Delete(path); }
                catch (IOException ex) { Log.Debug("Cleanup failed (locked): {Error}", LogService.SanitizePath(ex.Message)); }
                catch (UnauthorizedAccessException ex) { Log.Debug("Cleanup failed (access): {Error}", LogService.SanitizePath(ex.Message)); }
            }
            return !File.Exists(path);
        }, ct).ConfigureAwait(false);

        var exe = Path.Join(toolsDir, "speedtest.exe");
        if (!needsDownload)
        {
            // TOCTOU guard: the cached exe lives in a user-writable dir, so an attacker
            // could swap it between runs. Re-verify its Authenticode signature every
            // time before we hand it back to be executed — not only right after download.
            // The pin travels back with it, so the path cannot change meaning between here
            // and Process.Start.
            return await Task.Run(() => PinAndVerify(exe), ct).ConfigureAwait(false);
        }

        progress?.Report((5, "Downloading Ookla CLI…"));
        var arch = Environment.Is64BitOperatingSystem ? "win64" : "win32";
        // MAINTENANCE: Ookla CLI version is pinned. When a new version is released,
        // update the version string below. Check https://www.speedtest.net/apps/cli
        // for the latest version. Authenticode signature verification (below) ensures
        // binary integrity regardless of version.
        var zipUrl = $"https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-{arch}.zip";

        var zipPath = Path.Join(toolsDir, "ookla.zip");
        using (var resp = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        // Log download integrity info for audit (SHA-256 of the zip).
        // Security relies on Authenticode signature verification of the
        // extracted binary, not on pinned hashes (which break on Ookla updates).
        await Task.Run(() =>
        {
            using var stream = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            Log.Information("Ookla CLI downloaded: {Url}, SHA256={Hash}, Size={Size}",
                zipUrl, hash, new FileInfo(zipPath).Length);

            // Structural integrity check: must be a valid zip with speedtest.exe
            try
            {
                using var testZip = ZipFile.OpenRead(zipPath);
                if (!testZip.Entries.Any(e => e.Name.Equals("speedtest.exe", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Downloaded zip does not contain speedtest.exe");
            }
            catch (InvalidDataException)
            {
                File.Delete(zipPath);
                throw new InvalidOperationException("Downloaded Ookla CLI zip is corrupt or tampered");
            }
        }, ct).ConfigureAwait(false);

        progress?.Report((15, "Extracting…"));
        await Task.Run(() =>
        {
            // SEC-M3: Manual extraction with Zip Slip protection.
            // ZipFile.ExtractToDirectory does not validate that entry paths
            // stay within the target directory — a crafted zip with "../"
            // entries could write files outside toolsDir.
            using var archive = ZipFile.OpenRead(zipPath);
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
            File.Delete(zipPath);
        }, ct).ConfigureAwait(false);

        if (!File.Exists(exe))
            throw new FileNotFoundException("speedtest.exe not found after extraction");

        // Verify Authenticode signature on the freshly-extracted binary — fail-closed, and pinned first
        // for the same reason as the cached branch above. Extraction has just written this file into a
        // directory the user can write to, so the gap between verifying it and executing it is exactly as
        // exploitable on the first run as on every later one.
        return await Task.Run(() => PinAndVerify(exe), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that <paramref name="exe"/> carries a valid Authenticode signature whose subject is
    /// Ookla's — fail-closed, throwing on any mismatch or missing signature.
    /// </summary>
    /// <remarks>
    /// Deleting the rejected binary is <see cref="PinAndVerify"/>'s job, not this method's. It used to
    /// delete here, which stopped working the moment the file was pinned against deletion during
    /// verification: <see cref="TryDeleteExe"/> swallows the resulting IOException at Debug level, so a
    /// binary that failed verification would have stayed in the cache while the exception still claimed it
    /// had been removed. The caller releases the pin and then deletes.
    /// <para>Called on every cached reuse as well as right after download, since the cache lives in a
    /// user-writable directory.</para>
    /// </remarks>
    private static void VerifyOoklaSignature(string exe)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is obsolete — no direct replacement for Authenticode verification
            var signer = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exe);
#pragma warning restore SYSLIB0057
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(signer);

            if (!cert.Subject.Contains("Ookla", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Ookla speedtest.exe Authenticode subject mismatch: {Subject}", cert.Subject);
                throw new InvalidOperationException(
                    $"Ookla speedtest.exe failed Authenticode verification (subject: {cert.Subject}). Binary deleted for security.");
            }

            // Subject alone is forgeable (anyone can issue a self-signed "Ookla" cert),
            // so also build and validate the full certificate chain to a trusted root,
            // with online revocation. Fail closed if the chain does not validate.
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = System.Security.Cryptography.X509Certificates.X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag;
            if (!chain.Build(cert))
            {
                var statuses = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
                Log.Warning("Ookla speedtest.exe certificate chain did not validate: {Status}", statuses);
                throw new InvalidOperationException(
                    $"Ookla speedtest.exe certificate chain failed validation ({statuses}). Binary deleted for security.");
            }
            Log.Information("Ookla speedtest.exe Authenticode chain verified: {Subject}", cert.Subject);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log.Warning(ex, "Ookla speedtest.exe has no valid Authenticode signature");
            throw new InvalidOperationException(
                "Ookla speedtest.exe has no valid Authenticode signature. Binary deleted for security.", ex);
        }
    }

    private static void TryDeleteExe(string exe)
    {
        try { File.Delete(exe); }
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
