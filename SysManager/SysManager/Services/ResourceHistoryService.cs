// SysManager · ResourceHistoryService — always-on sampler that records system vitals to disk
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Records CPU, RAM and (best-effort) GPU usage plus CPU/GPU temperatures at a fixed
/// interval for the whole app lifetime, so the user can scroll back through hours or
/// days of history. Samples are appended one-per-line as NDJSON to
/// <c>%LocalAppData%\SysManager\resource-history.ndjson</c> with a configurable
/// retention window (7 / 14 / 30 days). Strictly local: nothing is written to the
/// system and nothing leaves the machine.
/// </summary>
public sealed class ResourceHistoryService : IDisposable
{
    /// <summary>How often a sample is taken. Matches the issue's "every 5-10 seconds".</summary>
    public const int SampleIntervalSeconds = 10;

    /// <summary>Retention options offered in the UI (days).</summary>
    public static readonly int[] RetentionOptions = [7, 14, 30];

    // Prune (rewrite to drop expired lines) at most once an hour while running, so a
    // long-lived session doesn't let the file grow past the retention window on disk.
    private const int PruneEverySamples = 360; // 360 × 10s = 1 hour

    private static readonly string DataDir = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
    private static readonly string DataPath = Path.Join(DataDir, "resource-history.ndjson");
    private static readonly string ConfigPath = Path.Join(DataDir, "resource-history-config.json");

    private static readonly JsonSerializerOptions SampleJson = new() { WriteIndented = false };

    private readonly SystemInfoService _sys;
    private readonly TemperatureService _temps;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private int _samplesSincePrune;

    // GPU usage is NVIDIA-only and the adapter availability is static for a session —
    // initialise the vendor API at most once, exactly like DashboardViewModel does.
    private bool _nvApiInitTried;
    private bool _nvApiAvailable;

    private int _retentionDays = 7;

    public ResourceHistoryService(SystemInfoService sys, TemperatureService temps)
    {
        _sys = sys;
        _temps = temps;
        _retentionDays = LoadRetention();
    }

    /// <summary>Days of history to keep. Persisted; shrinking it prunes the file.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            var clamped = RetentionOptions.Contains(value) ? value : 7;
            if (clamped == _retentionDays) return;
            _retentionDays = clamped;
            SaveRetention(clamped);
            _ = PruneAsync();
        }
    }

    public DateTime? LastSampleAt { get; private set; }

    /// <summary>
    /// Starts the background sampling loop. Idempotent — a second call is a no-op while a
    /// loop is already running. Called once at app startup so history accrues even when the
    /// window is hidden to the tray.
    /// </summary>
    public void Start()
    {
        if (_disposed || _cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Prune stale/malformed lines from a previous session before accruing new ones.
        _ = PruneAsync();

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var sample = await CaptureSampleAsync(ct).ConfigureAwait(false);
                    await AppendAsync(sample, ct).ConfigureAwait(false);
                    LastSampleAt = sample.Timestamp;

                    if (++_samplesSincePrune >= PruneEverySamples)
                    {
                        _samplesSincePrune = 0;
                        await PruneAsync().ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(SampleIntervalSeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Debug("Resource history sampling error: {Error}", ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(SampleIntervalSeconds), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    private async Task<ResourceSample> CaptureSampleAsync(CancellationToken ct)
    {
        var snap = await _sys.CaptureAsync(ct).ConfigureAwait(false);
        double? gpuPercent = ReadGpuUsage();

        double? cpuTemp = null, gpuTemp = null;
        try
        {
            var readings = await _temps.ReadAllAsync().ConfigureAwait(false);
            cpuTemp = readings
                .Where(r => r.Component == "CPU" && r.TemperatureC.HasValue)
                .Select(r => r.TemperatureC).Max();
            gpuTemp = readings
                .Where(r => r.Component.StartsWith("GPU", StringComparison.Ordinal) && r.TemperatureC.HasValue)
                .Select(r => r.TemperatureC).DefaultIfEmpty(null).Max();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException)
        {
            Log.Debug("Resource history temperature read failed: {Error}", ex.Message);
        }

        return new ResourceSample(snap.CapturedAt, snap.Cpu.LoadPercent, snap.Memory.UsedPercent,
            gpuPercent, cpuTemp, gpuTemp);
    }

    private double? ReadGpuUsage()
    {
        if (!_nvApiInitTried)
        {
            _nvApiInitTried = true;
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                _nvApiAvailable = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs().Length > 0;
            }
            catch (Exception ex)
            {
                _nvApiAvailable = false;
                Log.Debug("Resource history: NVIDIA GPU API unavailable: {Error}", ex.Message);
            }
        }

        if (!_nvApiAvailable) return null;
        try
        {
            var gpus = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs();
            return gpus.Length > 0 ? gpus[0].UsageInformation.GPU.Percentage : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Resource history: NVIDIA GPU usage read failed: {Error}", ex.Message);
            return null;
        }
    }

    // ── Disk access ──────────────────────────────────────────────────────

    private async Task AppendAsync(ResourceSample sample, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDir);
            await File.AppendAllTextAsync(DataPath, Serialize(sample) + "\n", ct).ConfigureAwait(false);
        }
        catch (IOException ex) { Log.Debug("Resource history append failed: {Error}", ex.Message); }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// Loads samples newer than <paramref name="range"/> ago, oldest-first. Malformed lines
    /// are skipped. Returns an empty list when no history exists yet.
    /// </summary>
    public async Task<IReadOnlyList<ResourceSample>> LoadAsync(TimeSpan range, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DataPath)) return [];
            var lines = await File.ReadAllLinesAsync(DataPath, ct).ConfigureAwait(false);
            var cutoff = DateTime.Now - range;
            var samples = new List<ResourceSample>(lines.Length);
            foreach (var line in lines)
            {
                if (TryParse(line, out var s) && s!.Timestamp >= cutoff)
                    samples.Add(s);
            }
            samples.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return samples;
        }
        catch (IOException ex) { Log.Debug("Resource history load failed: {Error}", ex.Message); return []; }
        finally { _fileLock.Release(); }
    }

    /// <summary>Rewrites the file dropping samples older than the retention window and any malformed lines.</summary>
    public async Task PruneAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(DataPath)) return;
            var lines = await File.ReadAllLinesAsync(DataPath).ConfigureAwait(false);
            var kept = Prune(lines, DateTime.Now, TimeSpan.FromDays(_retentionDays));
            // Only rewrite when something actually changed, to avoid needless disk churn.
            if (kept.Count == lines.Length) return;
            await File.WriteAllLinesAsync(DataPath, kept).ConfigureAwait(false);
        }
        catch (IOException ex) { Log.Debug("Resource history prune failed: {Error}", ex.Message); }
        finally { _fileLock.Release(); }
    }

    // ── Pure helpers (unit-testable, no WPF / WMI / file IO) ───────────────

    /// <summary>Serializes one sample to a single NDJSON line.</summary>
    public static string Serialize(ResourceSample sample) => JsonSerializer.Serialize(sample, SampleJson);

    /// <summary>Parses one NDJSON line. Returns false (and null) for blank or malformed input.</summary>
    public static bool TryParse(string? line, out ResourceSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            sample = JsonSerializer.Deserialize<ResourceSample>(line, SampleJson);
            return sample is not null;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Returns the input lines that parse and are newer than <paramref name="now"/> minus retention, oldest-first.</summary>
    public static List<string> Prune(IEnumerable<string> lines, DateTime now, TimeSpan retention)
    {
        var cutoff = now - retention;
        var kept = new List<ResourceSample>();
        foreach (var line in lines)
            if (TryParse(line, out var s) && s!.Timestamp >= cutoff)
                kept.Add(s);
        kept.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return kept.Select(Serialize).ToList();
    }

    /// <summary>
    /// Reduces a sample series to at most <paramref name="maxPoints"/> evenly-spaced buckets,
    /// averaging usage within each bucket, so a multi-day series renders without choking the
    /// chart. Series at or below the cap are returned unchanged. Pure and order-preserving.
    /// </summary>
    public static IReadOnlyList<ResourceSample> Downsample(IReadOnlyList<ResourceSample> samples, int maxPoints)
    {
        if (maxPoints < 1) maxPoints = 1;
        if (samples.Count <= maxPoints) return samples;

        var result = new List<ResourceSample>(maxPoints);
        // Bucket by position so the time spacing stays uniform across the whole range.
        double bucketSize = samples.Count / (double)maxPoints;
        for (int b = 0; b < maxPoints; b++)
        {
            int start = (int)(b * bucketSize);
            int end = (int)((b + 1) * bucketSize);
            if (end <= start) end = start + 1;
            if (end > samples.Count) end = samples.Count;

            double cpu = 0, ram = 0, gpu = 0, ctemp = 0, gtemp = 0;
            int n = 0, gN = 0, ctN = 0, gtN = 0;
            for (int i = start; i < end; i++)
            {
                var s = samples[i];
                cpu += s.CpuPercent; ram += s.RamPercent; n++;
                if (s.GpuPercent.HasValue) { gpu += s.GpuPercent.Value; gN++; }
                if (s.CpuTempC.HasValue) { ctemp += s.CpuTempC.Value; ctN++; }
                if (s.GpuTempC.HasValue) { gtemp += s.GpuTempC.Value; gtN++; }
            }
            if (n == 0) continue;
            // The bucket's timestamp is its midpoint sample's, so the X axis stays truthful.
            var mid = samples[Math.Min(start + (end - start) / 2, samples.Count - 1)].Timestamp;
            result.Add(new ResourceSample(mid,
                Math.Round(cpu / n, 1),
                Math.Round(ram / n, 1),
                gN > 0 ? Math.Round(gpu / gN, 1) : null,
                ctN > 0 ? Math.Round(ctemp / ctN, 1) : null,
                gtN > 0 ? Math.Round(gtemp / gtN, 1) : null));
        }
        return result;
    }

    /// <summary>Renders a sample series as CSV with a header row. Empty cells for missing GPU/temperature values.</summary>
    public static string ToCsv(IEnumerable<ResourceSample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,CPU %,RAM %,GPU %,CPU Temp °C,GPU Temp °C");
        foreach (var s in samples)
        {
            sb.Append(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.CpuPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.RamPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.GpuPercent?.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.CpuTempC?.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(s.GpuTempC?.ToString("F1", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ── Retention config persistence ───────────────────────────────────────

    private sealed record RetentionConfig(int RetentionDays);

    private static int LoadRetention()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return 7;
            var cfg = JsonSerializer.Deserialize<RetentionConfig>(File.ReadAllText(ConfigPath));
            return cfg is not null && RetentionOptions.Contains(cfg.RetentionDays) ? cfg.RetentionDays : 7;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Debug("Resource history config load failed: {Error}", ex.Message);
            return 7;
        }
    }

    private static void SaveRetention(int days)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new RetentionConfig(days)));
        }
        catch (IOException ex) { Log.Debug("Resource history config save failed: {Error}", ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _fileLock.Dispose();
    }
}
