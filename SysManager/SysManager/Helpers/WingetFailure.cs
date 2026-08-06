// SysManager · WingetFailure — one place that translates winget outcomes into plain language
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Turns winget exit codes and a missing App Installer into sentences the target persona can act on.
/// <para>Three tabs run winget — App Updates, Uninstaller, and Bulk Installer — and they had drifted
/// into three different levels of care. Uninstaller translated its exit codes and caught the
/// missing-winget case; App Updates caught the missing case and reused Uninstaller's message; Bulk
/// Installer did neither, writing <c>Failed (exit 1618)</c> and raw OS exception text into the row.
/// The same underlying failure was explained on two tabs and shown as a number on the third.</para>
/// <para>Everything lives here so a fourth caller cannot reintroduce the drift. The
/// install and uninstall maps are kept SEPARATE: winget reports different codes for the two
/// operations, so sharing one map would produce confidently wrong sentences.</para>
/// </summary>
public static class WingetFailure
{
    /// <summary>
    /// Shown when winget itself is missing. Plain-language so the user knows the tab needs App
    /// Installer, not that something broke.
    /// </summary>
    public const string WingetUnavailable =
        "winget (App Installer) isn't available on this PC — install \"App Installer\" from the Microsoft Store to use this tab.";

    /// <summary>
    /// Explains why an INSTALL failed, and what to do next. Codes are the ones winget actually
    /// returns for installs: the MSI set (1602/1603/1618/1619/1620/1638), Windows access denied (5),
    /// and winget's own cancelled/no-applicable-installer results.
    /// </summary>
    public static string DescribeInstallFailure(int exitCode)
    {
        var reason = exitCode switch
        {
            5 => "Access denied — retry and accept the installer's Windows UAC prompt.",
            1602 => "The installation was cancelled.",
            1603 => "The installer hit a fatal error. It may already be partly installed — check Windows Settings ▸ Apps.",
            1618 => "Another installation is already in progress — wait for it to finish and try again.",
            1619 => "The installer package could not be opened; the download may be corrupt.",
            1620 => "The installer package is not valid.",
            1638 => "Another version of this app is already installed — remove it first, or update it instead.",
            // winget's own results, reported as unsigned values.
            unchecked((int)0x8A150011) => "The installation was cancelled.",
            unchecked((int)0x8A150010) => "No installer for this app matches this PC.",
            unchecked((int)0x8A15002B) => "No suitable installer was found for this app.",
            unchecked((int)0x8A150044) => "The download failed — check the connection and try again.",
            _ => $"The installer returned code {exitCode}.",
        };

        return $"Failed — {reason}";
    }

    /// <summary>
    /// Explains why an UNINSTALL failed. Kept distinct from the install map on purpose: 1602 means
    /// "cancelled" for both, but most other codes do not correspond.
    /// </summary>
    public static string DescribeUninstallFailure(int exitCode)
    {
        var reason = exitCode switch
        {
            1 => "The app's uninstaller reported a generic error.",
            2 => "The uninstall was cancelled by the user or a UAC prompt was declined.",
            5 => "Access denied - retry and accept the uninstaller's Windows UAC prompt, or remove the app from Windows Settings.",
            87 => "Invalid parameter — the app may require a manual uninstall.",
            1602 => "The uninstall was cancelled by the user.",
            1603 => "The app's installer encountered a fatal error during removal.",
            1605 => "The app is not currently installed (already removed?).",
            1618 => "Another installation is in progress — wait and try again.",
            _ => $"The app's uninstaller returned exit code {exitCode}.",
        };

        return $"Failed — {reason}";
    }
}
