using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
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

    // 25 MB is a good compromise between accuracy and test duration on slow links.
    private const long PayloadBytes = 25L * 1024 * 1024;

    public async Task<SpeedTestResult> RunHttpAsync(
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        progress?.Report((0, "Pinging test server…"));
        var pingMs = await MeasurePingAsync(CfPingHost);

        progress?.Report((5, "Measuring download…"));
        var downloadMbps = await MeasureDownloadAsync(progress, ct);

        progress?.Report((55, "Measuring upload…"));
        var uploadMbps = await MeasureUploadAsync(progress, ct);

        progress?.Report((100, "Done"));
        return new SpeedTestResult("HTTP", downloadMbps, uploadMbps, pingMs, CfPingHost, DateTime.Now);
    }

    private static async Task<double> MeasurePingAsync(string host)
    {
        try
        {
            using var p = new Ping();
            var samples = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                var r = await p.SendPingAsync(host, 2000);
                if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime);
            }
            return samples.Count > 0 ? samples.Average() : 0;
        }
        catch { return 0; }
    }

    private static async Task<double> MeasureDownloadAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var url = string.Format(CfDownloadUrl, PayloadBytes);
        var sw = Stopwatch.StartNew();
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = 0;
        var buffer = new byte[81920];
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            var pct = 5 + (int)(total * 45 / PayloadBytes);
            progress?.Report((Math.Min(pct, 50), $"Download: {total / 1024 / 1024} MB"));
        }
        sw.Stop();

        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return total * 8.0 / 1_000_000.0 / seconds;
    }

    private static async Task<double> MeasureUploadAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var payload = new byte[PayloadBytes];
        new Random().NextBytes(payload);

        var sw = Stopwatch.StartNew();
        using var content = new ByteArrayContent(payload);
        using var resp = await _http.PostAsync(CfUploadUrl, content, ct);
        sw.Stop();

        // Some responses are rejected with 4xx on POST size — treat as best-effort.
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        progress?.Report((95, $"Upload complete: {payload.Length / 1024 / 1024} MB"));
        return payload.Length * 8.0 / 1_000_000.0 / seconds;
    }

    // ---------------- Ookla ----------------

    public async Task<SpeedTestResult> RunOoklaAsync(
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        string exe;
        try
        {
            exe = await EnsureOoklaAsync(progress, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not prepare Ookla CLI: {ex.Message}", ex);
        }

        progress?.Report((20, "Running Ookla speedtest…"));
        var psi = new ProcessStartInfo(exe,
            "--accept-license --accept-gdpr --format=json --progress=no")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false   // suppress Win32 "DLL not found" system dialogs
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start speedtest.exe");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            // If the exe is broken/corrupt, delete it so next run re-downloads it.
            if (proc.ExitCode == -1073741515) // STATUS_DLL_NOT_FOUND
            {
                try { File.Delete(exe); } catch { }
            }
            throw new InvalidOperationException($"Ookla failed ({proc.ExitCode}): {stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
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

    private static async Task<string> EnsureOoklaAsync(
        IProgress<(int, string)>? progress, CancellationToken ct)
    {
        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "tools");
        Directory.CreateDirectory(toolsDir);
        var exe = Path.Combine(toolsDir, "speedtest.exe");
        // Delete and re-download if the file exists but is suspiciously small (corrupt/partial).
        if (File.Exists(exe) && new FileInfo(exe).Length < 1024)
        {
            try { File.Delete(exe); } catch { }
        }
        if (File.Exists(exe)) return exe;

        progress?.Report((5, "Downloading Ookla CLI…"));
        var arch = Environment.Is64BitOperatingSystem ? "win64" : "win32";
        var zipUrl = $"https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-{arch}.zip";

        var zipPath = Path.Combine(toolsDir, "ookla.zip");
        using (var resp = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        progress?.Report((15, "Extracting…"));
        ZipFile.ExtractToDirectory(zipPath, toolsDir, overwriteFiles: true);
        File.Delete(zipPath);

        if (!File.Exists(exe))
            throw new FileNotFoundException("speedtest.exe not found after extraction");
        return exe;
    }
}
