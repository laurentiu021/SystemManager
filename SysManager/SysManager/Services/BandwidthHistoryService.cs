// SysManager · BandwidthHistoryService — persists total-throughput samples for the history graph
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Appends machine-wide total download/upload rate samples to
/// <c>%LocalAppData%\SysManager\bandwidth-history.ndjson</c> (one JSON object per line) so the
/// Bandwidth Monitor can draw the last hour/day/week. Mirrors <see cref="ResourceHistoryService"/>'s
/// proven append/prune/downsample model, but is driven by the tab's poll loop (only while the tab
/// is open) rather than an always-on sampler — bandwidth history is only interesting while the user
/// is watching it, and this avoids a second permanent background writer. Strictly local; nothing
/// leaves the machine.
/// </summary>
public sealed class BandwidthHistoryService
{
    /// <summary>Retention window in days. A week of the tab being open is ample for this graph.</summary>
    public const int RetentionDays = 7;

    private static readonly string DataDir = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
    private static readonly string DataPath = Path.Join(DataDir, "bandwidth-history.ndjson");

    private static readonly JsonSerializerOptions SampleJson = new() { WriteIndented = false };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>Appends one sample. Best-effort — an IO error is logged and swallowed.</summary>
    public async Task AppendAsync(BandwidthSample sample, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DataDir);
            await File.AppendAllTextAsync(DataPath, Serialize(sample) + "\n", ct).ConfigureAwait(false);
        }
        catch (IOException ex) { Log.Debug("Bandwidth history append failed: {Error}", ex.Message); }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// Loads samples newer than <paramref name="range"/> ago, oldest-first. Walks from the end
    /// (the file is append-ordered) so the work is bounded by the window, not the whole file.
    /// </summary>
    public async Task<IReadOnlyList<BandwidthSample>> LoadAsync(TimeSpan range, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DataPath)) return [];
            var lines = await File.ReadAllLinesAsync(DataPath, ct).ConfigureAwait(false);
            var cutoff = DateTime.Now - range;
            var samples = new List<BandwidthSample>();
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!TryParse(lines[i], out var s)) continue;
                if (s!.Timestamp < cutoff) break;
                samples.Add(s);
            }
            samples.Reverse();
            return samples;
        }
        catch (IOException ex) { Log.Debug("Bandwidth history load failed: {Error}", ex.Message); return []; }
        finally { _fileLock.Release(); }
    }

    /// <summary>Rewrites the file dropping samples older than the retention window and malformed lines.</summary>
    public async Task PruneAsync(CancellationToken ct = default)
    {
        try { await _fileLock.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!File.Exists(DataPath)) return;
            var lines = await File.ReadAllLinesAsync(DataPath, ct).ConfigureAwait(false);
            var kept = Prune(lines, DateTime.Now, TimeSpan.FromDays(RetentionDays));
            if (kept.Count == lines.Length) return;
            var tmp = DataPath + ".tmp";
            await File.WriteAllLinesAsync(tmp, kept, ct).ConfigureAwait(false);
            File.Move(tmp, DataPath, overwrite: true);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException ex) { Log.Debug("Bandwidth history prune failed: {Error}", ex.Message); }
        finally { _fileLock.Release(); }
    }

    // ── Pure helpers (unit-testable, no WPF / file IO) ─────────────────────

    /// <summary>Serializes one sample to a single NDJSON line.</summary>
    public static string Serialize(BandwidthSample sample) => JsonSerializer.Serialize(sample, SampleJson);

    /// <summary>Parses one NDJSON line. Returns false (and null) for blank or malformed input.</summary>
    public static bool TryParse(string? line, out BandwidthSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            sample = JsonSerializer.Deserialize<BandwidthSample>(line, SampleJson);
            return sample is not null;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Returns the input lines that parse and are newer than <paramref name="now"/> minus retention, oldest-first.</summary>
    public static List<string> Prune(IEnumerable<string> lines, DateTime now, TimeSpan retention)
    {
        var cutoff = now - retention;
        var kept = new List<BandwidthSample>();
        foreach (var line in lines)
            if (TryParse(line, out var s) && s!.Timestamp >= cutoff)
                kept.Add(s);
        kept.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return kept.Select(Serialize).ToList();
    }

    /// <summary>
    /// Reduces a sample series to at most <paramref name="maxPoints"/> evenly-spaced buckets,
    /// averaging the rates within each bucket, so a multi-day series renders without choking the
    /// chart. Series at or below the cap are returned unchanged. Pure and order-preserving.
    /// </summary>
    public static IReadOnlyList<BandwidthSample> Downsample(IReadOnlyList<BandwidthSample> samples, int maxPoints)
    {
        if (maxPoints < 1) maxPoints = 1;
        if (samples.Count <= maxPoints) return samples;

        var result = new List<BandwidthSample>(maxPoints);
        double bucketSize = samples.Count / (double)maxPoints;
        for (int b = 0; b < maxPoints; b++)
        {
            int start = (int)(b * bucketSize);
            int end = (int)((b + 1) * bucketSize);
            if (end <= start) end = start + 1;
            if (end > samples.Count) end = samples.Count;

            double down = 0, up = 0;
            int n = 0;
            for (int i = start; i < end; i++)
            {
                down += samples[i].DownBytesPerSec;
                up += samples[i].UpBytesPerSec;
                n++;
            }
            if (n == 0) continue;
            var mid = samples[Math.Min(start + (end - start) / 2, samples.Count - 1)].Timestamp;
            result.Add(new BandwidthSample(mid, down / n, up / n));
        }
        return result;
    }
}
