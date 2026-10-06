// SysManager · ExtensionPermissions
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Helpers;

/// <summary>
/// Turns what a browser extension asks for into the plain-language lines Browser Cleaner's Extensions view shows
/// (#1526), in a fixed order: first what changes what the user sees (her search engine, her home or new tab page),
/// then what it can read, then everything else by its own name.
/// </summary>
/// <remarks>
/// Curated, so it will lag behind permissions browsers add later. Nothing is dropped for that: a permission the
/// list does not know is shown as "Other: <c>name</c>", by the name the extension gave it.
/// </remarks>
internal static class ExtensionPermissions
{
    /// <summary>The line for an extension that can reach every website, which the list also sorts first.</summary>
    internal const string EverySite = "Can read and change everything on every website";

    /// <summary>How many named sites the line lists before "and N more sites".</summary>
    internal const int SitesNamed = 3;

    /// <summary>What an extension declares, from either browser family.</summary>
    /// <param name="Permissions">Its API permissions. Older Chromium extensions list sites here too; those are read as sites.</param>
    /// <param name="Sites">The site patterns it can reach: host permissions, content-script matches, granted origins.</param>
    internal sealed record Request(
        IEnumerable<string> Permissions,
        IEnumerable<string> Sites,
        bool ChangesSearch = false,
        bool ReplacesHomePage = false,
        bool ReplacesNewTab = false,
        bool ChangesStartupPages = false);

    /// <summary>
    /// The API permissions with a plain-language line, in the order they are shown. Everything else is "Other".
    /// </summary>
    private static readonly (string Text, bool Warning, Func<string, bool> Matches)[] Curated =
    [
        ("Can read your browsing history", false, p => p == "history"),
        ("Can see your open tabs", false, p => p == "tabs"),
        ("Can read your cookies", false, p => p == "cookies"),
        ("Can block or change web traffic", false,
            p => p.StartsWith("webRequest", StringComparison.Ordinal)
                 || p.StartsWith("declarativeNetRequest", StringComparison.Ordinal)),
        ("Can control the browser like a developer tool", true, p => p == "debugger"),
        ("Can talk to a program on this PC", false, p => p == "nativeMessaging"),
        ("Can manage your downloads", false,
            p => p == "downloads" || p.StartsWith("downloads.", StringComparison.Ordinal)),
    ];

    /// <summary>The lines for <paramref name="request"/>, and whether it can reach every website.</summary>
    internal static (IReadOnlyList<ExtensionPermission> Lines, bool CanReadEverySite) Describe(Request request)
    {
        List<ExtensionPermission> lines = [];
        if (request.ChangesSearch) lines.Add(new("Changes your search engine", true));
        if (request.ReplacesHomePage && request.ReplacesNewTab)
            lines.Add(new("Replaces your home page and new tab page", true));
        else if (request.ReplacesHomePage)
            lines.Add(new("Replaces your home page", true));
        else if (request.ReplacesNewTab)
            lines.Add(new("Replaces your new tab page", true));
        if (request.ChangesStartupPages) lines.Add(new("Changes the pages that open when the browser starts", true));

        var permissions = request.Permissions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        var sites = request.Sites.Concat(permissions.Where(IsSitePattern)).ToList();

        var everySite = sites.Any(IsEverySite);
        if (everySite)
        {
            lines.Add(new(EverySite, true));
        }
        else
        {
            var hosts = sites.Select(HostOf)
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hosts.Count > 0)
                lines.Add(new(NamedSites(hosts), false));
        }

        var apis = permissions.Where(p => !IsSitePattern(p)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var (text, warning, matches) in Curated)
        {
            if (apis.Any(matches)) lines.Add(new(text, warning));
        }
        foreach (var other in apis.Where(a => !Curated.Any(c => c.Matches(a))))
            lines.Add(new($"Other: {other}", false));

        return (lines, everySite);
    }

    private static string NamedSites(List<string> hosts)
    {
        var named = string.Join(", ", hosts.Take(SitesNamed));
        var more = hosts.Count - SitesNamed;
        return more > 0
            ? $"Can read and change data on {named} and {more} more site{(more == 1 ? "" : "s")}"
            : $"Can read and change data on {named}";
    }

    /// <summary>A site pattern rather than an API permission: <c>&lt;all_urls&gt;</c>, or anything with a scheme.</summary>
    internal static bool IsSitePattern(string value) =>
        value == "<all_urls>" || value.Contains("://", StringComparison.Ordinal);

    /// <summary>A pattern that matches every website: <c>&lt;all_urls&gt;</c>, or a web scheme with the host <c>*</c>.</summary>
    internal static bool IsEverySite(string pattern) =>
        pattern == "<all_urls>" || (IsWebScheme(pattern) && HostOf(pattern) == "*");

    /// <summary>
    /// The site a web pattern names, without a leading <c>*.</c> or a port: <c>*://*.google.com/*</c> →
    /// <c>google.com</c>. Empty for anything that is not a web page — files, other extensions, the browser's own.
    /// </summary>
    internal static string HostOf(string pattern)
    {
        if (!IsWebScheme(pattern)) return "";
        var rest = pattern[(pattern.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = rest.IndexOf('/');
        var host = slash >= 0 ? rest[..slash] : rest;
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
        return host.ToLowerInvariant();
    }

    private static bool IsWebScheme(string pattern) =>
        pattern.StartsWith("*://", StringComparison.Ordinal)
        || pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
}
