// SysManager · ExtensionPresenter
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>
/// The words the Extensions view uses, worked out from what the service read (#1526). Pure, so every sentence is
/// unit-tested without a profile; only <see cref="Icon"/> touches WPF, and it only decodes bytes it is handed.
/// </summary>
internal static class ExtensionPresenter
{
    /// <summary>A row for <paramref name="extension"/>, with its icon decoded.</summary>
    internal static ExtensionRow Row(BrowserExtension extension) => new(
        extension.Name,
        extension.Version,
        Meta(extension),
        Flags(extension),
        extension.Permissions,
        Icon(extension.IconBytes));

    /// <summary>
    /// The line under the name: when it was installed and where from, or why only someone else can remove it.
    /// </summary>
    /// <param name="zone">The time zone the day is given in: the user's own unless a test passes one.</param>
    internal static string Meta(BrowserExtension extension, TimeZoneInfo? zone = null)
    {
        var installed = extension.InstalledOn is { } on ? $"Installed {Date(on, zone)}" : null;
        return extension.Origin switch
        {
            ExtensionOrigin.Browser => "Came with the browser",
            ExtensionOrigin.Folder => "Loaded from a folder on this PC",
            ExtensionOrigin.Organisation => installed is null
                ? "Your organisation manages this one, so only it can remove it"
                : $"{installed} · your organisation manages this one, so only it can remove it",
            _ => Sentence(string.Join(" · ", new[] { installed, Source(extension) }.Where(p => p is not null))),
        };
    }

    // The line can start with the source when there is no date; it still reads as a sentence.
    private static string Sentence(string line) =>
        line.Length > 0 ? char.ToUpperInvariant(line[0]) + line[1..] : line;

    private static string? Source(BrowserExtension extension) =>
        extension.Store.Length > 0 ? $"from {extension.Store}"
        : extension.Origin is ExtensionOrigin.File or ExtensionOrigin.AnotherProgram ? "not from an extension store"
        : null;

    /// <summary>The day, as "29 Sep 2026", in the user's own time zone unless a test passes another.</summary>
    /// <remarks>
    /// The zone is a parameter so a test's day does not depend on the machine it runs on: no instant falls on the
    /// same calendar day in every zone, since they span 26 hours.
    /// </remarks>
    internal static string Date(DateTime utc, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The marks next to the name: who added it, when that was not the user, and whether it is off.</summary>
    internal static IReadOnlyList<ExtensionFlag> Flags(BrowserExtension extension)
    {
        List<ExtensionFlag> flags = [];
        if (extension.Origin == ExtensionOrigin.AnotherProgram)
            flags.Add(new("Added by another program", ExtensionFlagKind.AnotherProgram));
        if (extension.Origin == ExtensionOrigin.Organisation)
            flags.Add(new("Added by your organisation", ExtensionFlagKind.Organisation));
        if (extension.IsOff) flags.Add(new("Off", ExtensionFlagKind.Off));
        return flags;
    }

    /// <summary>
    /// The toolbar's summary: how many extensions in how many browsers, and how many another program put there —
    /// the number that answers "it installed itself". Empty when there are none.
    /// </summary>
    internal static string Summary(IReadOnlyList<ExtensionProfile> profiles)
    {
        var extensions = profiles.Sum(p => p.Extensions.Count);
        if (extensions == 0) return "";
        var browsers = profiles.Where(p => p.Extensions.Count > 0).Select(p => p.Product).Distinct(StringComparer.Ordinal).Count();
        var another = profiles.Sum(p => p.Extensions.Count(e => e.Origin == ExtensionOrigin.AnotherProgram));
        var summary = $"{extensions} extension{(extensions == 1 ? "" : "s")} in {browsers} browser{(browsers == 1 ? "" : "s")}";
        return another > 0
            ? $"{summary} · {another} {(another == 1 ? "was" : "were")} put there by another program"
            : summary;
    }

    /// <summary>
    /// The status line after "Manage in …". Whether the browser opened on the page cannot be seen from here, so the
    /// address is put on the clipboard too, and the line says what to do with it. When nothing was started because
    /// SysManager runs as administrator, it says so: otherwise the button would look broken. For a profile the tab
    /// names, such as "Google Chrome — Profile 2", it names the profile, since the page shows the extensions of the
    /// profile it is opened in.
    /// </summary>
    internal static string ManageStatus(ExtensionProfile profile, ExtensionsPageOpening opening, bool copied)
    {
        var named = profile.Browser.StartsWith(profile.Product + " — ", StringComparison.Ordinal)
            ? profile.Browser[(profile.Product.Length + 3)..]
            : null;
        var where = named is null ? profile.Product : $"{profile.Product}'s \"{named}\" profile";
        const string elevated = " SysManager is running as administrator, so a browser it opened would run as administrator too.";
        return (opening, copied) switch
        {
            (ExtensionsPageOpening.Opened, true) => $"Opened {profile.Product}. If its extensions page did not open, paste {profile.Page} into the address bar — it is on your clipboard.",
            (ExtensionsPageOpening.Opened, false) => $"Opened {profile.Product}. If its extensions page did not open, type {profile.Page} into the address bar.",
            (ExtensionsPageOpening.NotWhileElevated, true) => $"Open {where} yourself and paste {profile.Page} into its address bar — it is on your clipboard." + elevated,
            (ExtensionsPageOpening.NotWhileElevated, false) => $"Open {where} yourself and type {profile.Page} into its address bar." + elevated,
            (_, true) => named is null
                ? $"Paste {profile.Page} into {profile.Product}'s address bar to see its extensions — it is on your clipboard."
                : $"Paste {profile.Page} into the address bar of {where} to see its extensions — it is on your clipboard.",
            (_, false) => named is null
                ? $"Type {profile.Page} into {profile.Product}'s address bar to see its extensions."
                : $"Type {profile.Page} into the address bar of {where} to see its extensions.",
        };
    }

    /// <summary>
    /// The icon decoded and frozen, so it can be built off the UI thread; null when there is none or it is not an
    /// image WPF can read, and the row shows the puzzle glyph instead.
    /// </summary>
    internal static ImageSource? Icon(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Both sides, as AppIconService does: by width alone an icon one pixel wide keeps its shape, and a few
            // hundred bytes decode to an image hundreds of times the size the row draws, kept with the row.
            image.DecodePixelWidth = 56;
            image.DecodePixelHeight = 56;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                      or ArgumentException or IOException or ExternalException or OverflowException)
        {
            // Not an image WPF can read, or one its codec refused: the row shows the puzzle glyph instead.
            Log.Debug(ex, "An extension's icon could not be decoded");
            return null;
        }
    }
}
