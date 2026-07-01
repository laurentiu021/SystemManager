// SysManager · UpdateService — GitHub releases client
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
    public const string Repo = "SystemManager";

    /// <summary>
    /// True for the release's main executable asset. Release assets are named
    /// <c>SysManager-v&lt;version&gt;.exe</c> (e.g. <c>SysManager-v1.20.1.exe</c>),
    /// not a fixed <c>SysManager.exe</c>, so match by pattern and exclude the
    /// companion <c>.sha256</c> checksum file.
    /// </summary>
    public static bool IsMainExeAsset(string? assetName) =>
        !string.IsNullOrEmpty(assetName) &&
        assetName.StartsWith("SysManager-", StringComparison.OrdinalIgnoreCase) &&
        assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static readonly HttpClient Http = CreateClient();

    // Version-suffix separators (pre-release / build / trailing space). Hoisted to a
    // SearchValues so ParseVersion's scan doesn't allocate a char[] per call.
    private static readonly SearchValues<char> VersionSuffixSeparators = SearchValues.Create("-+ ");

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
                if (dto is null) { LastError = "GitHub returned an empty response."; return null; }
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
            if (dto is null) return [];
            return dto.Select(Map).OfType<ReleaseInfo>().ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (HttpRequestException ex)
        {
            Serilog.Log.Warning(ex, "Failed to fetch recent releases (network)");
            return [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            Serilog.Log.Warning(ex, "Failed to parse recent releases JSON");
            return [];
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

        var dir = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Join(dir, $"SysManager-{rel.Version}.exe");

        // SEC-M2: Skip re-download only if we have a cached hash that matches.
        // File size alone is insufficient — an attacker could replace the binary
        // with a same-size payload. We store a companion .sha256 file after each
        // successful download to enable fast cache validation without re-downloading
        // the hash from GitHub on every launch.
        var hashFile = target + ".sha256";
        if (File.Exists(target) && File.Exists(hashFile))
        {
            try
            {
                var cachedHash = (await File.ReadAllTextAsync(hashFile, ct).ConfigureAwait(false)).Trim();
                if (cachedHash.Length >= 64)
                {
                    var actualHash = await Task.Run(() =>
                    {
                        using var stream = File.OpenRead(target);
                        return Convert.ToHexString(SHA256.HashData(stream));
                    }, ct).ConfigureAwait(false);

                    if (string.Equals(cachedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        return target;

                    // Hash mismatch — cached file is corrupt or tampered, re-download.
                    Serilog.Log.Warning("Cached update binary hash mismatch — re-downloading");
                }
            }
            catch (IOException) { /* hash file unreadable — re-download */ }
            catch (UnauthorizedAccessException) { /* hash file unreadable — re-download */ }
        }

        var tempFile = target + ".tmp";
        try
        {
            using var resp = await Http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? rel.AssetSize;

            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(tempFile);

            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await net.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report((read, total));
            }

            // Flush before hashing.
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Close();

            // Compute SHA-256 on the completed temp file.
            var downloadedHash = await Task.Run(() =>
            {
                using var hashStream = File.OpenRead(tempFile);
                return Convert.ToHexString(SHA256.HashData(hashStream));
            }, ct).ConfigureAwait(false);

            // Atomic move: temp → final target.
            File.Move(tempFile, target, overwrite: true);

            // SEC-M2: Store SHA-256 hash of the downloaded binary for cache validation.
            try
            {
                await File.WriteAllTextAsync(hashFile, downloadedHash, ct).ConfigureAwait(false);
            }
            catch (IOException) { /* non-fatal — next launch will re-download */ }
            catch (UnauthorizedAccessException) { /* non-fatal */ }

            return target;
        }
        catch (OperationCanceledException)
        {
            // Only clean the partial in-progress temp file. `target` may be a
            // previously-downloaded, still-valid cached binary — deleting it on a
            // cancelled re-download would force an avoidable re-download next launch.
            CleanupFile(tempFile);
            return null;
        }
        catch (HttpRequestException ex)
        {
            Serilog.Log.Warning(ex, "Failed to download release asset (network)");
            CleanupFile(tempFile);
            return null;
        }
        catch (IOException ex)
        {
            Serilog.Log.Warning(ex, "Failed to write release asset to disk");
            CleanupFile(tempFile);
            return null;
        }
    }

    /// <summary>
    /// Downloads the .sha256 file for a release and verifies the local file matches.
    /// Returns true if the hash matches, false if mismatch or if the .sha256 file
    /// is unavailable (verification is best-effort — network errors don't block install).
    /// </summary>
    public async Task<(bool Verified, string? ExpectedHash, string? ActualHash)> VerifyHashAsync(
        ReleaseInfo rel, string filePath, CancellationToken ct = default)
    {
        try
        {
            var sha256Url = $"https://github.com/{Owner}/{Repo}/releases/download/{rel.Tag}/SysManager-{rel.Tag}.exe.sha256";
            var hashText = await Http.GetStringAsync(sha256Url, ct).ConfigureAwait(false);

            // .sha256 file format: "HASH  filename" or just "HASH"
            var expectedHash = hashText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length < 64)
                return (false, null, null);

            var actualHash = await Task.Run(() =>
            {
                using var stream = File.OpenRead(filePath);
                var hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash);
            }, ct).ConfigureAwait(false);

            var match = string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
            if (!match)
                Serilog.Log.Warning("SHA256 mismatch for {File}: expected {Expected}, got {Actual}",
                    LogService.SanitizePath(filePath), expectedHash, actualHash);
            else
                Serilog.Log.Information("SHA256 verified for {File}: {Hash}", LogService.SanitizePath(filePath), actualHash);

            return (match, expectedHash, actualHash);
        }
        catch (HttpRequestException ex)
        {
            Serilog.Log.Warning(ex, "Could not download .sha256 file for verification");
            return (false, null, null); // SEC-001: treat missing hash as verification failure
        }
        catch (OperationCanceledException)
        {
            return (false, null, null);
        }
        catch (IOException ex)
        {
            Serilog.Log.Warning(ex, "Could not read downloaded file for hash verification");
            return (false, null, null);
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

    // CryptographicException HResult raised by CreateFromSignedFile when the file
    // carries NO embedded Authenticode signature at all. On .NET this surfaces as
    // CRYPT_E_NO_MATCH (0x80092009) — "Cannot find the requested object". SysManager's
    // own builds are unsigned (no code-signing certificate yet), so this is the normal,
    // expected case and must NOT be treated as tampering.
    private const int CryptENoMatch = unchecked((int)0x80092009);

    /// <summary>
    /// Reports the Authenticode state of a downloaded update binary. Returns true when
    /// the binary is validly embedded-signed OR carries no signature at all (SysManager
    /// ships unsigned open-source builds). Returns false only when reading the signature
    /// fails for a reason other than "no signature present".
    ///
    /// This is NOT the integrity gate: file integrity is enforced by the SHA256 check
    /// (<see cref="VerifyHashAsync"/>) against the published .sha256, which runs first in
    /// the install flow. <c>CreateFromSignedFile</c> extracts the signer certificate but
    /// does not by itself validate the file against the signature, so it cannot detect a
    /// tampered signed binary — the SHA256 comparison is what catches a modified download.
    /// </summary>
    public static bool VerifyAuthenticode(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is obsolete
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            // A non-null cert means an embedded Authenticode signature was found and its
            // signer certificate could be read. (Unsigned files throw below rather than
            // returning null, so this branch is the signed case.)
            Serilog.Log.Information("Update binary is Authenticode-signed: {Subject}", cert.Subject);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex) when (ex.HResult == CryptENoMatch)
        {
            // No embedded signature — expected for SysManager's unsigned builds. Allow it;
            // integrity is already guaranteed by the SHA256 verification step.
            Serilog.Log.Information("Update binary has no Authenticode signature (expected for unsigned builds)");
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Signature data present but could not be read/parsed — treat as suspect.
            Serilog.Log.Warning(ex, "Update binary Authenticode signature could not be read (HResult 0x{HResult:X8}): {File}",
                ex.HResult, LogService.SanitizePath(filePath));
            return false;
        }
    }

    private static void CleanupFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { Serilog.Log.Debug(ex, "Update cleanup: could not delete {Path}", LogService.SanitizePath(path)); }
        catch (UnauthorizedAccessException ex) { Serilog.Log.Debug(ex, "Update cleanup: access denied deleting {Path}", LogService.SanitizePath(path)); }
    }

    // ---------- internals ----------

    private static ReleaseInfo? Map(GhRelease dto)
    {
        if (dto.TagName is null) return null;
        if (dto.Prerelease || dto.Draft) return null;
        var version = ParseVersion(dto.TagName);
        if (version is null) return null;

        var asset = dto.Assets?.FirstOrDefault(a => IsMainExeAsset(a.Name));

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
        var cut = s.AsSpan().IndexOfAny(VersionSuffixSeparators);
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public GhAsset[]? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
