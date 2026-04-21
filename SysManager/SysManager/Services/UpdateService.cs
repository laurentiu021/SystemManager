// SysManager · UpdateService — GitHub releases client
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("SysManager.Tests")]

namespace SysManager.Services;

/// <summary>
/// Talks to the GitHub Releases API to discover new SysManager builds.
/// Works entirely against the public REST endpoint — no auth needed as
/// long as we stay under the anonymous rate limit (60 req/hour/IP).
/// </summary>
public sealed class UpdateService
{
    public const string Owner = "laurentiu021";
    public const string Repo  = "SysManager";
    public const string AssetName = "SysManager.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Explicit handler so TLS and redirect behaviour are deterministic.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SysManager-UpdateCheck/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public sealed record ReleaseInfo(
        Version Version,
        string Tag,
        string Name,
        string Body,
        DateTimeOffset PublishedAt,
        string HtmlUrl,
        string? AssetUrl,
        long? AssetSize);

    /// <summary>Human-readable reason the last call failed. Empty on success.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Fetches the most recent release. Returns null on any network/parse
    /// error; the reason is stored in <see cref="LastError"/>. Retries
    /// once on transient failures so a single flaky socket doesn't
    /// surface as an error to the user.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        LastError = string.Empty;
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var dto = await Http.GetFromJsonAsync<GhRelease>(url, ct).ConfigureAwait(false);
                if (dto == null) { LastError = "GitHub returned an empty response."; return null; }
                return Map(dto);
            }
            catch (OperationCanceledException)
            {
                LastError = "Check cancelled.";
                return null;
            }
            catch (HttpRequestException ex)
            {
                LastError = $"Network: {ex.Message}";
                if (attempt == 2) return null;
                await Task.Delay(800, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = $"Unexpected: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Fetches the last N releases (for a full changelog view).
    /// </summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetRecentAsync(int count = 10, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page={count}";
            var dto = await Http.GetFromJsonAsync<GhRelease[]>(url, ct).ConfigureAwait(false);
            if (dto == null) return Array.Empty<ReleaseInfo>();
            return dto.Select(Map).OfType<ReleaseInfo>().ToList();
        }
        catch
        {
            return Array.Empty<ReleaseInfo>();
        }
    }

    /// <summary>
    /// True when the latest release is strictly newer than the running app.
    /// </summary>
    public static bool IsNewer(Version latest, Version current) => latest > current;

    /// <summary>
    /// Downloads the release asset with progress reporting. Returns the
    /// path to the downloaded file, or null on failure / cancellation.
    /// Stored under %LOCALAPPDATA%\SysManager\updates.
    /// </summary>
    public async Task<string?> DownloadAsync(
        ReleaseInfo rel,
        IProgress<(long bytesRead, long? total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rel.AssetUrl)) return null;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"SysManager-{rel.Version}.exe");

        // Skip re-download if we already have a good copy.
        if (File.Exists(target) && rel.AssetSize.HasValue && new FileInfo(target).Length == rel.AssetSize.Value)
            return target;

        try
        {
            using var resp = await Http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? rel.AssetSize;

            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(target);

            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await net.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report((read, total));
            }

            return target;
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            return null;
        }
    }

    /// <summary>
    /// The version compiled into this running assembly. Falls back to 0.0.0.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v ?? new Version(0, 0, 0);
        }
    }

    // ---------- internals ----------

    private static ReleaseInfo? Map(GhRelease dto)
    {
        if (dto.TagName == null) return null;
        var version = ParseVersion(dto.TagName);
        if (version == null) return null;

        var asset = dto.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));

        return new ReleaseInfo(
            Version: version,
            Tag: dto.TagName,
            Name: dto.Name ?? dto.TagName,
            Body: dto.Body ?? string.Empty,
            PublishedAt: dto.PublishedAt ?? DateTimeOffset.MinValue,
            HtmlUrl: dto.HtmlUrl ?? $"https://github.com/{Owner}/{Repo}/releases/tag/{dto.TagName}",
            AssetUrl: asset?.BrowserDownloadUrl,
            AssetSize: asset?.Size);
    }

    /// <summary>
    /// Extracts a <see cref="Version"/> from a GitHub release tag.
    /// Exposed publicly so tests can exercise it without going through
    /// the network layer.
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        // Accept "v0.4.0", "0.4.0", "v0.4.0-beta" — strip at most one leading v/V.
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];
        // Reject if still starts with a letter (e.g. "vv1.2.3" → "v1.2.3" → still starts with v).
        if (s.Length == 0 || char.IsLetter(s[0])) return null;
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]     public string? TagName { get; set; }
        [JsonPropertyName("name")]         public string? Name { get; set; }
        [JsonPropertyName("body")]         public string? Body { get; set; }
        [JsonPropertyName("html_url")]     public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")]   public bool Prerelease { get; set; }
        [JsonPropertyName("draft")]        public bool Draft { get; set; }
        [JsonPropertyName("assets")]       public GhAsset[]? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]                 public string? Name { get; set; }
        [JsonPropertyName("size")]                 public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
