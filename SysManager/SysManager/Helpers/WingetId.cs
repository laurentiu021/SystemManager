// SysManager · WingetId — single source of truth for winget package-ID validation
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;

namespace SysManager.Helpers;

/// <summary>
/// Validates winget package IDs before they are interpolated into a winget
/// command line. This is a security boundary: the same allowlist was previously
/// copy-pasted into WingetService, UninstallerService and BulkInstallerService,
/// where three copies could drift apart. Centralising it here keeps the rule in
/// one place.
/// </summary>
public static partial class WingetId
{
    /// <summary>
    /// True if <paramref name="packageId"/> is a valid winget package ID: a
    /// non-blank string of alphanumerics, dots, hyphens, underscores, forward
    /// slashes (scoped IDs like "Microsoft.VisualStudio.2022.Community"), plus
    /// signs (e.g. "Notepad++.Notepad++") and spaces, up to 256 characters.
    /// </summary>
    public static bool IsValid(string? packageId) =>
        !string.IsNullOrWhiteSpace(packageId) && Pattern().IsMatch(packageId);

    // \A…\z (absolute anchors) rather than ^…$: ^/$ would let a trailing newline
    // through ("pkg\n" matches as "pkg"), which could smuggle a second line into
    // the winget argument. \z anchors at the true end of input. A literal space is
    // used instead of \s (which would also allow tabs/newlines mid-string).
    [GeneratedRegex(@"\A[\w.\-/+ ]{1,256}\z")]
    private static partial Regex Pattern();

    /// <summary>
    /// True if <paramref name="packageId"/> is one of the LOCAL ids winget prints for an app no source
    /// matches — a packaged app as <c>MSIX\&lt;PackageFullName&gt;</c>, or a registry entry as
    /// <c>ARP\Machine|User\&lt;arch&gt;\&lt;key&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>A rule of its own, NOT a relaxation of <see cref="IsValid"/>: that allowlist guards every other
    /// winget command line in the app, and these ids need a backslash and braces, which it rightly refuses.
    /// Only <c>UninstallAsync</c> accepts this shape, and only because <c>winget uninstall --id</c> accepts
    /// these ids itself — without it, the apps winget lists this way could be ticked and confirmed and then
    /// never removed, which is most of the Store apps on a normal machine (#2849).</para>
    /// <para>The shapes were derived from 103 real local ids (59 <c>MSIX\</c>, 44 <c>ARP\</c>) on a live
    /// machine, not from the documentation: MSIX is always exactly two segments whose tail holds only
    /// alphanumerics, dots, hyphens and underscores; ARP is always exactly four, with braces and parentheses
    /// in the key. Not one of the 103 contained a quote or any shell metacharacter, which is what makes a rule
    /// this tight sufficient as well as safe.</para>
    /// <para>Two deliberate widenings beyond what those 103 showed, both so a machine this could not be tested
    /// on is not refused by a list: the architecture segment is a general alphanumeric token rather than the
    /// <c>X64</c>/<c>X86</c> seen here, so an ARM machine's <c>ARM64</c> works; and the ARP key may contain a
    /// SPACE, because a registry key name is arbitrary text and <see cref="IsValid"/> has always allowed spaces
    /// for ordinary ids — a space inside <c>--id "…"</c> cannot split the argument. The MSIX tail stays without
    /// one, since a PackageFullName never has a space by Windows' own format.</para>
    /// <para>What the pattern refuses, since the id is interpolated into <c>--id "{id}"</c>: a double quote
    /// (would close the argument early), a trailing backslash (would escape the closing quote — the one that
    /// looks harmless and is not), any control character, and any extra segment beyond the structure above.</para>
    /// </remarks>
    public static bool IsLocalUninstallId(string? packageId) =>
        !string.IsNullOrWhiteSpace(packageId) && LocalPattern().IsMatch(packageId);

    // Same \A…\z discipline as above, and every character class excludes the backslash, so the segment
    // count is exactly what is written here and no id can end on one. A literal space in the ARP key, not
    // \s, which would also admit a tab or a newline.
    [GeneratedRegex(@"\A(?:MSIX\\[\w.\-]{1,250}|ARP\\(?:Machine|User)\\[A-Za-z0-9]{1,16}\\[\w.\-(){} ]{1,230})\z")]
    private static partial Regex LocalPattern();
}
