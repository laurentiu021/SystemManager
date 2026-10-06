// SysManager · BrowserExtension
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Where an extension came from, as the browser itself recorded it (#1526). This is the part that answers "it
/// installed itself": an extension another program put there is recorded as such, and the list can say so.
/// </summary>
public enum ExtensionOrigin
{
    /// <summary>The browser did not say, or its record could not be read.</summary>
    Unknown = 0,

    /// <summary>The user added it from an extension store.</summary>
    Store,

    /// <summary>The user added it, but not from an extension store — from a file, for example.</summary>
    File,

    /// <summary>Another program on the PC asked the browser to add it.</summary>
    AnotherProgram,

    /// <summary>The organisation that manages the PC added it, and only it can remove it.</summary>
    Organisation,

    /// <summary>It came with the browser.</summary>
    Browser,

    /// <summary>It is loaded from a folder on the PC, the way a developer tries one out.</summary>
    Folder,
}

/// <summary>One plain-language line about what an extension can do, and whether it deserves attention.</summary>
public sealed record ExtensionPermission(string Text, bool IsWarning)
{
    /// <summary>The line's words, which is what a screen reader announces for it.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// One extension installed in one browser profile, as Browser Cleaner's Extensions view lists it (#1526). Read from
/// the browser's own files; SysManager never changes them.
/// </summary>
/// <param name="Store">The store it came from ("the Chrome Web Store"), or empty when it did not come from one.</param>
/// <param name="CanReadEverySite">True when it can read and change every website, which the list sorts first.</param>
/// <param name="IconBytes">The extension's own icon file, or null when it has none: the list then shows a glyph.</param>
public sealed record BrowserExtension(
    string Name,
    string Version,
    ExtensionOrigin Origin,
    bool IsOff,
    DateTime? InstalledOn,
    string Store,
    IReadOnlyList<ExtensionPermission> Permissions,
    bool CanReadEverySite,
    byte[]? IconBytes);

/// <summary>
/// One browser profile's extensions, with what is needed to open that browser on its own extensions page.
/// </summary>
/// <param name="Browser">The name the tab shows for the profile, e.g. "Microsoft Edge — Profile 1".</param>
/// <param name="Product">The browser alone, e.g. "Microsoft Edge", for "Manage in …" and "Close … and look again".</param>
/// <param name="Page">The browser's own extensions page, e.g. "edge://extensions".</param>
/// <param name="Executable">What starts the browser, e.g. "msedge.exe", or null when it cannot be started reliably.</param>
/// <param name="ProfileDirectory">
/// The profile folder to open it in, such as "Default" or "Profile 2", or null for a browser that is not opened in one
/// (Opera, Firefox) or whose profiles could not be read.
/// </param>
/// <param name="CouldNotRead">True when the profile's extension list could not be read, so an empty list is not "none".</param>
/// <param name="ProfileName">
/// The profile's own name when "Manage in …" must say which profile to open — a browser with more than one profile,
/// or a Firefox profile Firefox does not open by itself — and null otherwise.
/// </param>
/// <param name="BehindALink">True when the profile was not read because it sits behind a link, which is not followed.</param>
public sealed record ExtensionProfile(
    string Browser,
    string Product,
    string Page,
    string? Executable,
    string? ProfileDirectory,
    IReadOnlyList<BrowserExtension> Extensions,
    bool CouldNotRead,
    string? ProfileName = null,
    bool BehindALink = false);

/// <summary>What "Manage in …" did when asked to open a browser on its extensions page (#1526).</summary>
public enum ExtensionsPageOpening
{
    /// <summary>
    /// Nothing was started: the browser has no reliable way to be started on the page, or it did not start. The
    /// default, so an answer nobody set claims nothing.
    /// </summary>
    NotOpened = 0,

    /// <summary>The browser was started on its extensions page.</summary>
    Opened,

    /// <summary>Nothing was started because SysManager runs as administrator, and the browser would too.</summary>
    NotWhileElevated,
}
