// SysManager · AppIconService — downloads and caches app icons from the internet
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Downloads application icons (favicons) from the internet and caches them locally.
/// Uses the Google favicon service to retrieve PNGs for any domain.
/// Falls back gracefully when no icon is available or no internet connection exists.
///
/// <para>The HTTP transport is injectable (an <see cref="HttpMessageHandler"/>),
/// so the download path can be unit-tested with a stubbed handler instead of
/// hitting the live internet.</para>
/// </summary>
public sealed class AppIconService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysManager", "IconCache");

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the service. Pass a custom <paramref name="handler"/> (e.g. a stub)
    /// to redirect the download transport in tests; production uses the default
    /// handler with a 5-second timeout.
    /// </summary>
    public AppIconService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SysManager/1.0");
    }

    /// <summary>
    /// Gets the icon for an application by its winget ID.
    /// Returns a cached icon if available, otherwise downloads from the internet.
    /// Returns null if the icon cannot be obtained.
    /// </summary>
    public async Task<BitmapImage?> GetIconAsync(string appId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var cachePath = Path.Combine(CacheDir, $"{SanitizeFileName(appId)}.png");

        // Return cached icon if it exists and is not empty
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            return LoadFromFile(cachePath);

        // Determine the download URL
        var url = GetFaviconUrl(appId);
        if (url == null) return null;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
            return LoadFromFile(cachePath);
        }
        catch (HttpRequestException ex)
        {
            Log.Debug(ex, "Icon download failed for {AppId}", appId);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            Log.Debug(ex, "Icon download timed out or was cancelled for {AppId}", appId);
            return null;
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "Could not write icon cache for {AppId}", appId);
            return null;
        }
    }

    private static string? GetFaviconUrl(string appId)
    {
        if (AppDomains.TryGetValue(appId, out var domain))
            return $"https://www.google.com/s2/favicons?domain={domain}&sz=32";
        return null;
    }

    private static BitmapImage? LoadFromFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 32;
            bitmap.DecodePixelHeight = 32;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            // Corrupt/unreadable cache file or an unsupported image format — skip gracefully.
            Log.Debug(ex, "Could not load cached icon from {Path}", path);
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    /// <summary>
    /// Maps winget package IDs to their associated website domain for favicon retrieval.
    /// </summary>
    private static readonly Dictionary<string, string> AppDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        ["Google.Chrome"] = "google.com/chrome",
        ["Mozilla.Firefox"] = "mozilla.org",
        ["Brave.Brave"] = "brave.com",
        ["Vivaldi.Vivaldi"] = "vivaldi.com",

        // Communication
        ["Discord.Discord"] = "discord.com",
        ["SlackTechnologies.Slack"] = "slack.com",
        ["Zoom.Zoom"] = "zoom.us",
        ["Telegram.TelegramDesktop"] = "telegram.org",
        ["WhatsApp.WhatsApp"] = "whatsapp.com",
        ["Microsoft.Teams"] = "teams.microsoft.com",

        // Media
        ["VideoLAN.VLC"] = "videolan.org",
        ["Spotify.Spotify"] = "spotify.com",
        ["PeterPawlowski.foobar2000"] = "foobar2000.org",

        // Development
        ["Microsoft.VisualStudioCode"] = "code.visualstudio.com",
        ["Git.Git"] = "git-scm.com",
        ["OpenJS.NodeJS.LTS"] = "nodejs.org",
        ["Python.Python.3.12"] = "python.org",

        // Utilities
        ["7zip.7zip"] = "7-zip.org",
        ["Notepad++.Notepad++"] = "notepad-plus-plus.org",
        ["voidtools.Everything"] = "voidtools.com",
        ["Microsoft.PowerToys"] = "github.com/microsoft/PowerToys",
        ["RARLab.WinRAR"] = "win-rar.com",
        ["ShareX.ShareX"] = "getsharex.com",
        ["Greenshot.Greenshot"] = "getgreenshot.org",
        ["JAMSoftware.TreeSize.Free"] = "jam-software.com",

        // Gaming
        ["Valve.Steam"] = "store.steampowered.com",
        ["EpicGames.EpicGamesLauncher"] = "epicgames.com",
        ["GOG.Galaxy"] = "gog.com",

        // Security
        ["Bitwarden.Bitwarden"] = "bitwarden.com",
        ["Malwarebytes.Malwarebytes"] = "malwarebytes.com",

        // Office & Productivity
        ["TheDocumentFoundation.LibreOffice"] = "libreoffice.org",
        ["Obsidian.Obsidian"] = "obsidian.md",
        ["Notion.Notion"] = "notion.so",
        ["Adobe.Acrobat.Reader.64-bit"] = "adobe.com",

        // Creativity
        ["OBSProject.OBSStudio"] = "obsproject.com",
        ["GIMP.GIMP"] = "gimp.org",
        ["Audacity.Audacity"] = "audacityteam.org",
        ["BlenderFoundation.Blender"] = "blender.org",

        // Networking & VPN
        ["qBittorrent.qBittorrent"] = "qbittorrent.org",
        ["ProtonTechnologies.ProtonVPN"] = "protonvpn.com",
        ["WireGuard.WireGuard"] = "wireguard.com",
        ["SimonTatham.PuTTY"] = "putty.org",

        // Runtimes & Frameworks
        ["Microsoft.DotNet.DesktopRuntime.8"] = "dotnet.microsoft.com",
        ["Microsoft.VCRedist.2015+.x64"] = "microsoft.com",
        ["Oracle.JavaRuntimeEnvironment"] = "java.com",
        ["Microsoft.DirectX"] = "microsoft.com",
    };
}
