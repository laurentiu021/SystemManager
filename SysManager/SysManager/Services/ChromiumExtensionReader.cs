// SysManager · ChromiumExtensionReader
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads one Chromium profile's extensions (Chrome, Edge, Brave, Vivaldi, Opera) for Browser Cleaner's Extensions
/// view (#1526). Read-only: nothing in the profile is changed.
/// </summary>
/// <remarks>
/// Two sources, as the browser keeps them. Each extension has a folder under the profile's <c>Extensions</c>, with
/// one subfolder per version holding its <c>manifest.json</c>: its name, version, icon and what it asks for. The
/// profile's <c>Secure Preferences</c> adds what the manifest cannot say: whether it is turned off, when it was
/// first installed, and where it came from — a store, the organisation's policy, the browser itself, or another
/// program on the PC. The settings are optional: when they cannot be read, each extension is still listed from its
/// manifest, with nothing claimed about its origin. Extensions built into the browser (the component ones) have no
/// folder in the profile and are not listed, as the browser's own extensions page does not list them either; one
/// loaded from a folder elsewhere on the PC is found through the settings, which record its path.
/// <para>Nothing is left out for being unreadable. An extension whose manifest cannot be read, or whose folder is a
/// link, is listed as <see cref="UnreadableName"/> with whatever its settings say, and an <c>Extensions</c> folder
/// that cannot be listed makes the profile one that could not be read. A link where an extension or one of its
/// folders should be is never followed, and a path the settings give off this PC's drives is never opened. Every
/// value is read through <see cref="ExtensionFiles"/>, so one the parser let through but cannot be read is simply
/// absent.</para>
/// </remarks>
internal static class ChromiumExtensionReader
{
    /// <summary>What the list calls an extension whose name could not be read from its files.</summary>
    internal const string UnreadableName = "An extension whose name could not be read";

    /// <summary>The settings Chromium keeps per extension, as far as the list uses them.</summary>
    private sealed record Settings(int? Location, bool FromWebstore, bool CameWithBrowser, bool IsOff,
        DateTime? InstalledOn, string? Path);

    /// <summary>
    /// The profile's extensions, and whether the list could not be read. A profile with no <c>Extensions</c> folder
    /// has none, which is not a failure.
    /// </summary>
    /// <param name="culture">The language names are looked up in first; the extension's own default after that.</param>
    internal static (IReadOnlyList<BrowserExtension> Extensions, bool CouldNotRead) Read(string profileDir, CultureInfo culture)
    {
        var settings = ReadSettings(profileDir);
        List<BrowserExtension> found = [];
        var couldNotRead = false;

        var extensionsDir = System.IO.Path.Combine(profileDir, "Extensions");
        switch (ExtensionFiles.KindOf(extensionsDir))
        {
            case ExtensionFiles.EntryKind.Folder:
                string[] folders;
                try { folders = Directory.GetDirectories(extensionsDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The folder is there and cannot be listed: say so, and still look for the unpacked ones below.
                    Log.Debug(ex, "A browser's extension folder could not be listed");
                    couldNotRead = true;
                    break;
                }

                foreach (var folder in folders)
                {
                    var id = System.IO.Path.GetFileName(folder);
                    if (!IsExtensionId(id)) continue;
                    settings.TryGetValue(id, out var own);
                    // A link is not followed, and an extension that cannot be read is still listed: leaving either out
                    // would hide exactly what the list is for.
                    if (SafeFileWalk.IsReparsePoint(folder))
                    {
                        found.Add(Unreadable(own));
                        continue;
                    }
                    var (versionDir, unreadable) = NewestVersionFolder(folder);
                    if (unreadable) found.Add(Unreadable(own));
                    else if (versionDir is not null) found.Add(FromManifest(versionDir, own, culture) ?? Unreadable(own));
                }
                break;

            case ExtensionFiles.EntryKind.Missing:
                // A new profile, or one that never had an extension: not a failure.
                break;

            default:
                // A file, a link or something unreadable where the list should be: the browser could not use it, or
                // reads it from somewhere SysManager does not follow.
                couldNotRead = true;
                break;
        }

        // An extension loaded from a folder elsewhere on the PC is not under Extensions; its settings name the folder.
        foreach (var own in settings.Values)
        {
            if (own.Location is not (LocationUnpacked or LocationCommandLine)) continue;
            if (own.Path is not { Length: > 0 } path) continue;
            if (!ExtensionFiles.IsOnALocalDrive(path) || ExtensionFiles.HasAPartWindowsRenames(path))
            {
                found.Add(Unreadable(own));
                continue;
            }
            switch (ExtensionFiles.KindOf(path))
            {
                case ExtensionFiles.EntryKind.Folder:
                    found.Add(FromManifest(path, own, culture) ?? Unreadable(own));
                    break;
                case ExtensionFiles.EntryKind.Missing:
                    // Removed since it was loaded: the browser shows it as gone too.
                    break;
                default:
                    found.Add(Unreadable(own));
                    break;
            }
        }

        return (found, couldNotRead);
    }

    // Chromium's ManifestLocation values (extensions/common/mojom/manifest.mojom), as Secure Preferences stores them.
    private const int LocationInternal = 1;
    private const int LocationExternalPref = 2;
    private const int LocationExternalRegistry = 3;
    private const int LocationUnpacked = 4;
    private const int LocationComponent = 5;
    private const int LocationExternalPrefDownload = 6;
    private const int LocationExternalPolicyDownload = 7;
    private const int LocationCommandLine = 8;
    private const int LocationExternalPolicy = 9;
    private const int LocationExternalComponent = 10;

    /// <summary>
    /// Where an extension came from, from what the browser recorded. Pure and internal so each location is
    /// unit-tested without a profile.
    /// </summary>
    internal static ExtensionOrigin OriginOf(int? location, bool fromWebstore, bool cameWithBrowser) =>
        cameWithBrowser ? ExtensionOrigin.Browser : location switch
        {
            LocationInternal => fromWebstore ? ExtensionOrigin.Store : ExtensionOrigin.File,
            LocationExternalPref or LocationExternalRegistry or LocationExternalPrefDownload => ExtensionOrigin.AnotherProgram,
            LocationExternalPolicyDownload or LocationExternalPolicy => ExtensionOrigin.Organisation,
            LocationComponent or LocationExternalComponent => ExtensionOrigin.Browser,
            LocationUnpacked or LocationCommandLine => ExtensionOrigin.Folder,
            _ => ExtensionOrigin.Unknown,
        };

    /// <summary>The 32 letters a to p Chromium names an extension's folder with; anything else there is not one.</summary>
    internal static bool IsExtensionId(string name) =>
        name.Length == 32 && name.All(c => c is >= 'a' and <= 'p');

    /// <summary>
    /// The newest of an extension's version folders, compared number by number (<c>10.0_0</c> is newer than
    /// <c>9.1_0</c>), among those that hold a manifest; no folder when none does, which is a leftover rather than an
    /// extension. Unreadable when the folder cannot be listed, or when the newest is a link: it is not followed, and
    /// the browser loads the newest, so showing an older one in its place would show the wrong version and rights.
    /// </summary>
    internal static (string? Folder, bool Unreadable) NewestVersionFolder(string extensionFolder)
    {
        string[] versions;
        try { versions = Directory.GetDirectories(extensionFolder); }
        catch (IOException) { return (null, true); }
        catch (UnauthorizedAccessException) { return (null, true); }

        // A link counts by its name alone: looking for the manifest inside it would already be following it.
        var newest = versions
            .Where(v => SafeFileWalk.IsReparsePoint(v) || File.Exists(System.IO.Path.Combine(v, "manifest.json")))
            .OrderByDescending(v => VersionParts(System.IO.Path.GetFileName(v)), VersionComparer.Instance)
            .FirstOrDefault();
        return newest is not null && SafeFileWalk.IsReparsePoint(newest) ? (null, true) : (newest, false);
    }

    private static long[] VersionParts(string name) =>
        [.. name.Split('.', '_').Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)];

    private sealed class VersionComparer : IComparer<long[]>
    {
        internal static readonly VersionComparer Instance = new();

        public int Compare(long[]? x, long[]? y)
        {
            x ??= []; y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var a = i < x.Length ? x[i] : 0;
                var b = i < y.Length ? y[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            return 0;
        }
    }

    /// <summary>One extension from the manifest in <paramref name="folder"/>, with its settings when they were read.</summary>
    private static BrowserExtension? FromManifest(string folder, Settings? settings, CultureInfo culture)
    {
        using var manifest = ExtensionFiles.Parse(
            ExtensionFiles.Read(System.IO.Path.Combine(folder, "manifest.json"), ExtensionFiles.MaxManifestBytes));
        if (manifest?.RootElement is not { ValueKind: JsonValueKind.Object } root) return null;

        // A name that cannot be looked up still lists the extension: leaving it out would hide exactly what the list
        // is for.
        var name = Localised(folder, root, ExtensionFiles.String(root, "name"), culture)
                   ?? Localised(folder, root, ExtensionFiles.String(root, "short_name"), culture);
        if (string.IsNullOrWhiteSpace(name)) name = UnreadableName;
        var version = ExtensionFiles.String(root, "version_name") ?? ExtensionFiles.String(root, "version") ?? "";

        var overrides = ExtensionFiles.TryGet(root, "chrome_settings_overrides", out var o) ? o : default;
        var urlOverrides = ExtensionFiles.TryGet(root, "chrome_url_overrides", out var u) ? u : default;
        var (lines, everySite) = ExtensionPermissions.Describe(new(
            Permissions: ExtensionFiles.Strings(root, "permissions"),
            Sites: ExtensionFiles.Strings(root, "host_permissions").Concat(ContentScriptMatches(root)),
            ChangesSearch: ExtensionFiles.Has(overrides, "search_provider"),
            ReplacesHomePage: ExtensionFiles.Has(overrides, "homepage"),
            ReplacesNewTab: ExtensionFiles.Has(urlOverrides, "newtab"),
            ChangesStartupPages: ExtensionFiles.Has(overrides, "startup_pages")));

        var icon = ExtensionFiles.TryGet(root, "icons", out var icons) && ExtensionFiles.PickIcon(icons) is { } iconPath
                   && ExtensionFiles.Inside(folder, iconPath) is { } iconFile
            ? ExtensionFiles.Read(iconFile, ExtensionFiles.MaxIconBytes)
            : null;

        var fromStore = settings?.FromWebstore ?? false;
        return new BrowserExtension(
            Name: name.Trim(),
            Version: version,
            Origin: OriginOf(settings),
            IsOff: settings?.IsOff ?? false,
            InstalledOn: settings?.InstalledOn,
            Store: fromStore ? StoreOf(ExtensionFiles.String(root, "update_url")) : "",
            Permissions: lines,
            CanReadEverySite: everySite,
            IconBytes: icon);
    }

    private static ExtensionOrigin OriginOf(Settings? settings) => settings is null
        ? ExtensionOrigin.Unknown
        : OriginOf(settings.Location, settings.FromWebstore, settings.CameWithBrowser);

    /// <summary>
    /// An extension whose files could not be read, or are not read because they sit behind a link or off this PC's
    /// drives: listed as <see cref="UnreadableName"/>, with whatever its settings say about where it came from.
    /// </summary>
    private static BrowserExtension Unreadable(Settings? settings) => new(
        Name: UnreadableName,
        Version: "",
        Origin: OriginOf(settings),
        IsOff: settings?.IsOff ?? false,
        InstalledOn: settings?.InstalledOn,
        Store: "",
        Permissions: [],
        CanReadEverySite: false,
        IconBytes: null);

    private static IEnumerable<string> ContentScriptMatches(JsonElement root)
    {
        if (!ExtensionFiles.TryGet(root, "content_scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var script in scripts.EnumerateArray())
        {
            foreach (var match in ExtensionFiles.Strings(script, "matches")) yield return match;
        }
    }

    /// <summary>
    /// The store an extension's updates come from, by its update address: the Chrome Web Store, Microsoft Edge
    /// Add-ons or Opera's. An address the list does not know is "an extension store".
    /// </summary>
    internal static string StoreOf(string? updateUrl)
    {
        if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var uri)) return "an extension store";
        var host = uri.Host;
        if (ExtensionFiles.IsHostOf(host, "google.com")) return "the Chrome Web Store";
        if (ExtensionFiles.IsHostOf(host, "microsoft.com")) return "Microsoft Edge Add-ons";
        if (ExtensionFiles.IsHostOf(host, "opera.com")) return "Opera add-ons";
        return "an extension store";
    }

    /// <summary>
    /// The name to show: the manifest's own, or, for one written as <c>__MSG_key__</c>, the message from the
    /// extension's translation files — the user's language first, then its language without the region, then the
    /// extension's default. Keys are matched without regard to case, as the browser does.
    /// </summary>
    internal static string? Localised(string folder, JsonElement root, string? name, CultureInfo culture)
    {
        if (name is null || !name.StartsWith("__MSG_", StringComparison.Ordinal) || !name.EndsWith("__", StringComparison.Ordinal)
            || name.Length <= 8)
        {
            return name;
        }
        var key = name[6..^2];
        var defaultLocale = ExtensionFiles.String(root, "default_locale");
        string[] locales = [culture.Name.Replace('-', '_'), culture.TwoLetterISOLanguageName, defaultLocale ?? ""];
        foreach (var locale in locales.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ExtensionFiles.Inside(folder, $"_locales/{locale}/messages.json") is not { } file) continue;
            using var messages = ExtensionFiles.Parse(ExtensionFiles.Read(file, ExtensionFiles.MaxManifestBytes));
            if (messages?.RootElement is not { ValueKind: JsonValueKind.Object } table) continue;
            foreach (var (entry, value) in ExtensionFiles.Properties(table))
            {
                if (string.Equals(entry, key, StringComparison.OrdinalIgnoreCase)
                    && ExtensionFiles.String(value, "message") is { Length: > 0 } message)
                {
                    return message;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The profile's per-extension settings by id, from <c>Secure Preferences</c> (or <c>Preferences</c>, where
    /// older and other Chromium builds keep them). Empty when neither can be read.
    /// </summary>
    private static Dictionary<string, Settings> ReadSettings(string profileDir)
    {
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            using var document = ExtensionFiles.Parse(
                ExtensionFiles.Read(System.IO.Path.Combine(profileDir, file), ExtensionFiles.MaxSettingsBytes));
            if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !ExtensionFiles.TryGet(root, "extensions", out var extensions)
                || !ExtensionFiles.TryGet(extensions, "settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var byId = new Dictionary<string, Settings>(StringComparer.Ordinal);
            foreach (var (id, s) in ExtensionFiles.Properties(settings))
            {
                if (id is null || s.ValueKind != JsonValueKind.Object) continue;
                byId[id] = new Settings(
                    Location: ExtensionFiles.Int(s, "location"),
                    FromWebstore: ExtensionFiles.Bool(s, "from_webstore") ?? false,
                    CameWithBrowser: (ExtensionFiles.Bool(s, "was_installed_by_default") ?? false)
                                     || (ExtensionFiles.Bool(s, "was_installed_by_oem") ?? false),
                    IsOff: IsOff(s),
                    InstalledOn: InstallTime(s),
                    Path: ExtensionFiles.String(s, "path"));
            }
            if (byId.Count > 0) return byId;
        }
        return [];
    }

    /// <summary>
    /// Whether the browser keeps an extension turned off: any reason to disable it, as a list (current builds) or a
    /// bit mask (older ones), or the old <c>state</c> of 0.
    /// </summary>
    private static bool IsOff(JsonElement settings)
    {
        if (ExtensionFiles.TryGet(settings, "disable_reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
            return reasons.GetArrayLength() > 0;
        if (ExtensionFiles.Number(settings, "disable_reasons") is { } mask) return mask != 0;
        return ExtensionFiles.Int(settings, "state") == 0;
    }

    /// <summary>
    /// When the extension was first installed. Chromium writes the time as a string of microseconds since 1601 (the
    /// Windows file-time origin), so it converts exactly.
    /// </summary>
    internal static DateTime? InstallTime(JsonElement settings)
    {
        foreach (var name in new[] { "first_install_time", "install_time" })
        {
            if (ExtensionFiles.String(settings, name) is { } text
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
                && microseconds is > 0 and < MaxFileTimeMicroseconds)
            {
                return DateTime.FromFileTimeUtc(microseconds * 10);
            }
        }
        return null;
    }

    // DateTime.MaxValue as a file time, in microseconds: anything past it is not a time.
    private const long MaxFileTimeMicroseconds = 2_650_467_743_999_999_999 / 10;
}
