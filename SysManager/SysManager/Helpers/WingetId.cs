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
}
