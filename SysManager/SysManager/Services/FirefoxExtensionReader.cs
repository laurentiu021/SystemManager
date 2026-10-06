// SysManager · FirefoxExtensionReader
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads one Firefox profile's extensions for Browser Cleaner's Extensions view (#1526). Read-only: nothing in the
/// profile is changed.
/// </summary>
/// <remarks>
/// Firefox keeps the facts the list needs in the profile's <c>extensions.json</c>: each add-on's name, version,
/// type, whether it is on, when it was installed, where it came from and the permissions the user granted. Each
/// extension's own package (an <c>.xpi</c>, which is a zip) adds its icon and any search engine or home page it
/// replaces. Themes, dictionaries and language packs are add-ons too and are left out, and so are the add-ons
/// Firefox hides because they are part of the browser. A profile without the file has no extensions — a new
/// profile, or one never opened — which is not a failure; a file that exists and cannot be read is. Every value is
/// read through <see cref="ExtensionFiles"/>, so one the parser let through but cannot be read is simply absent.
/// </remarks>
internal static class FirefoxExtensionReader
{
    /// <summary>A package read for its icon and manifest. Real extensions are well under this.</summary>
    internal const long MaxPackageBytes = 64L << 20;

    /// <summary>The profile's extensions, and whether the list could not be read.</summary>
    internal static (IReadOnlyList<BrowserExtension> Extensions, bool CouldNotRead) Read(string profileDir)
    {
        var file = Path.Combine(profileDir, "extensions.json");
        if (!File.Exists(file)) return ([], false);

        using var document = ExtensionFiles.Parse(ExtensionFiles.Read(file, ExtensionFiles.MaxSettingsBytes));
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !ExtensionFiles.TryGet(root, "addons", out var addons)
            || addons.ValueKind != JsonValueKind.Array)
        {
            return ([], true);
        }

        List<BrowserExtension> found = [];
        foreach (var addon in addons.EnumerateArray())
        {
            if (addon.ValueKind != JsonValueKind.Object) continue;
            if (ExtensionFiles.String(addon, "type") != "extension") continue;
            if (ExtensionFiles.Bool(addon, "hidden") == true || ExtensionFiles.Bool(addon, "visible") == false) continue;
            found.Add(FromAddon(addon, profileDir));
        }
        return (found, false);
    }

    private static BrowserExtension FromAddon(JsonElement addon, string profileDir)
    {
        var name = ExtensionFiles.TryGet(addon, "defaultLocale", out var locale) ? ExtensionFiles.String(locale, "name") : null;
        var origin = OriginOf(
            ExtensionFiles.String(addon, "location"),
            ExtensionFiles.Bool(addon, "foreignInstall") ?? false,
            ExtensionFiles.TryGet(addon, "installTelemetryInfo", out var telemetry) ? ExtensionFiles.String(telemetry, "source") : null,
            ExtensionFiles.String(addon, "sourceURI"));

        var granted = ExtensionFiles.TryGet(addon, "userPermissions", out var g) ? g : default;
        var package = ReadPackage(ExtensionFiles.String(addon, "path"),
            ExtensionFiles.TryGet(addon, "icons", out var icons) ? ExtensionFiles.PickIcon(icons) : null, profileDir);
        var (lines, everySite) = ExtensionPermissions.Describe(new(
            Permissions: ExtensionFiles.Strings(granted, "permissions"),
            Sites: ExtensionFiles.Strings(granted, "origins"),
            ChangesSearch: package.ChangesSearch,
            ReplacesHomePage: package.ReplacesHomePage,
            ReplacesNewTab: package.ReplacesNewTab,
            ChangesStartupPages: package.ChangesStartupPages));

        var isOn = ExtensionFiles.Bool(addon, "active") == true && ExtensionFiles.Bool(addon, "userDisabled") != true;
        return new BrowserExtension(
            Name: string.IsNullOrWhiteSpace(name) ? ChromiumExtensionReader.UnreadableName : name.Trim(),
            Version: ExtensionFiles.String(addon, "version") ?? "",
            Origin: origin,
            IsOff: !isOn,
            InstalledOn: InstallDate(addon),
            Store: origin == ExtensionOrigin.Store ? "Firefox Add-ons" : "",
            Permissions: lines,
            CanReadEverySite: everySite,
            IconBytes: package.Icon);
    }

    // Where Firefox installs add-ons that another program placed for it (the "sideload" locations).
    private static readonly HashSet<string> OtherProgramLocations = new(StringComparer.Ordinal)
    {
        "app-global", "app-system-share", "app-system-local", "app-system-user", "winreg-app-user", "winreg-app-global",
    };

    /// <summary>
    /// Where an extension came from, from what Firefox recorded. Pure and internal so each case is unit-tested
    /// without a profile.
    /// </summary>
    /// <param name="location">The install location, e.g. <c>app-profile</c> for the user's own.</param>
    /// <param name="foreignInstall">Firefox's own flag for an add-on it found rather than installed.</param>
    /// <param name="source">How it was installed, e.g. <c>amo</c> for the add-ons site.</param>
    internal static ExtensionOrigin OriginOf(string? location, bool foreignInstall, string? source, string? sourceUri)
    {
        if (source == "enterprise-policy") return ExtensionOrigin.Organisation;
        if (location == "app-temporary" || source == "temporary-addon") return ExtensionOrigin.Folder;
        if (foreignInstall || (location is not null && OtherProgramLocations.Contains(location)))
            return ExtensionOrigin.AnotherProgram;
        if (location is not null
            && (location.StartsWith("app-builtin", StringComparison.Ordinal) || location.StartsWith("app-system", StringComparison.Ordinal)))
        {
            return ExtensionOrigin.Browser;
        }
        if (location == "app-profile")
        {
            var fromStore = source == "amo"
                || (Uri.TryCreate(sourceUri, UriKind.Absolute, out var uri) && ExtensionFiles.IsHostOf(uri.Host, "addons.mozilla.org"));
            return fromStore ? ExtensionOrigin.Store : ExtensionOrigin.File;
        }
        return ExtensionOrigin.Unknown;
    }

    /// <summary>When the add-on was installed: Firefox writes milliseconds since 1970.</summary>
    internal static DateTime? InstallDate(JsonElement addon) =>
        ExtensionFiles.Number(addon, "installDate") is { } ms && ms is > 0 and < MaxUnixMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;

    // DateTimeOffset.MaxValue in Unix milliseconds: anything past it is not a time.
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>What the extension's package adds: its icon, and the browser pages it replaces.</summary>
    private sealed record Package(byte[]? Icon, bool ChangesSearch, bool ReplacesHomePage, bool ReplacesNewTab,
        bool ChangesStartupPages);

    private static readonly Package NoPackage = new(null, false, false, false, false);

    /// <summary>
    /// Reads the icon at <paramref name="iconEntry"/> and the manifest from the package at <paramref name="path"/>.
    /// Only a <c>.xpi</c> file is opened, on one of this PC's drives or inside the profile being read — which, with
    /// Windows' folder redirection, can itself sit on a share — and nothing in it is extracted: the two entries are
    /// read into memory, each within its bound, and the package's own size is checked once it is open, so it cannot
    /// change in between. A package that cannot be read yields nothing, and the extension is still listed.
    /// </summary>
    private static Package ReadPackage(string? path, string? iconEntry, string profileDir)
    {
        if (path is null || !path.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase)
            || !(ExtensionFiles.IsOnALocalDrive(path) || ExtensionFiles.IsUnder(path, profileDir)))
        {
            return NoPackage;
        }
        try
        {
            if (!File.Exists(path) || SafeFileWalk.IsReparsePoint(path)) return NoPackage;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxPackageBytes) return NoPackage;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            using var manifest = ExtensionFiles.Parse(Entry(zip, "manifest.json", ExtensionFiles.MaxManifestBytes));
            var root = manifest?.RootElement ?? default;
            var overrides = ExtensionFiles.TryGet(root, "chrome_settings_overrides", out var o) ? o : default;
            var urlOverrides = ExtensionFiles.TryGet(root, "chrome_url_overrides", out var u) ? u : default;
            return new Package(
                Icon: iconEntry is null ? null : Entry(zip, iconEntry, ExtensionFiles.MaxIconBytes),
                ChangesSearch: ExtensionFiles.Has(overrides, "search_provider"),
                ReplacesHomePage: ExtensionFiles.Has(overrides, "homepage"),
                ReplacesNewTab: ExtensionFiles.Has(urlOverrides, "newtab"),
                ChangesStartupPages: ExtensionFiles.Has(overrides, "startup_pages"));
        }
        catch (IOException) { return NoPackage; }
        catch (UnauthorizedAccessException) { return NoPackage; }
        catch (InvalidDataException) { return NoPackage; }
    }

    /// <summary>One entry's bytes, or null when it is missing or larger than <paramref name="maxBytes"/>.</summary>
    private static byte[]? Entry(ZipArchive zip, string name, int maxBytes)
    {
        var entry = zip.GetEntry(name.TrimStart('/'));
        if (entry is null || entry.Length > maxBytes) return null;
        using var stream = entry.Open();
        var bytes = new byte[entry.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
