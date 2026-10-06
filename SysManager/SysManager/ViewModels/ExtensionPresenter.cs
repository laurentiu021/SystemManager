// SysManager · ExtensionPresenter
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    internal static string Meta(BrowserExtension extension)
    {
        var installed = extension.InstalledOn is { } on ? $"Installed {Date(on)}" : null;
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

    /// <summary>The day, as "29 Sep 2026", in the user's own time zone.</summary>
    internal static string Date(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

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
    /// SysManager runs as administrator, it says so: otherwise the button would look broken.
    /// </summary>
    internal static string ManageStatus(ExtensionProfile profile, ExtensionsPageOpening opening, bool copied) => (opening, copied) switch
    {
        (ExtensionsPageOpening.Opened, true) => $"Opened {profile.Product}. If its extensions page did not open, paste {profile.Page} into the address bar — it is on your clipboard.",
        (ExtensionsPageOpening.Opened, false) => $"Opened {profile.Product}. If its extensions page did not open, type {profile.Page} into the address bar.",
        (ExtensionsPageOpening.NotWhileElevated, true) => $"Open {profile.Product} yourself and paste {profile.Page} into its address bar — it is on your clipboard. SysManager is running as administrator, so a browser it opened would run as administrator too.",
        (ExtensionsPageOpening.NotWhileElevated, false) => $"Open {profile.Product} yourself and type {profile.Page} into its address bar. SysManager is running as administrator, so a browser it opened would run as administrator too.",
        (_, true) => $"Paste {profile.Page} into {profile.Product}'s address bar to see its extensions — it is on your clipboard.",
        (_, false) => $"Type {profile.Page} into {profile.Product}'s address bar to see its extensions.",
    };

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
            image.DecodePixelWidth = 56;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException) { return null; }
        catch (FileFormatException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }
}
