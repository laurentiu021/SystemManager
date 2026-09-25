// SysManager · WingetFailure — one place that translates winget outcomes into plain language
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

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
/// operations, so sharing one map would produce confidently wrong sentences. winget's own codes are
/// written as the named constants on <see cref="WingetExitCodes"/>, never as numbers (#2462).</para>
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
    /// Explains why an INSTALL failed, and what to do next. Covers the results winget itself ends an install
    /// on, plus the MSI set (1602/1603/1618/1619/1620/1638) and Windows access denied (5) for an installer
    /// whose own code reaches the row.
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
            WingetExitCodes.InstallCancelledByUser => "The installation was cancelled.",
            WingetExitCodes.InstallInstallInProgress =>
                "Another installation is already in progress — wait for it to finish and try again.",
            WingetExitCodes.InstallPackageInUse or WingetExitCodes.InstallPackageInUseByApplication
                or WingetExitCodes.InstallFileInUse => "The app is running — close it and try again.",
            WingetExitCodes.NoApplicableInstaller => "No installer for this app matches this PC.",
            WingetExitCodes.InstallerHashMismatch =>
                "The download did not match what winget expected, so it was not run. Try again later.",
            WingetExitCodes.DownloadFailed or WingetExitCodes.InstallNoNetwork =>
                "The download failed — check the connection and try again.",
            WingetExitCodes.SourceOpenFailed or WingetExitCodes.FailedToOpenAllSources =>
                "winget could not reach its package sources — check the connection and try again.",
            WingetExitCodes.InstallDiskFull => "There is not enough free disk space.",
            WingetExitCodes.InstallBlockedByPolicy => "A system policy blocks this installation.",
            WingetExitCodes.InstallRebootRequiredForInstall => "Windows needs a restart before this app can install.",
            WingetExitCodes.ShellExecInstallFailed or WingetExitCodes.MsiInstallFailed =>
                "The app's own installer reported an error.",
            _ when WingetExitCodes.IsWingetCode(exitCode) => $"winget stopped with code {WingetExitCodes.Hex(exitCode)}.",
            _ => $"The installer returned code {exitCode}.",
        };

        return $"Failed — {reason}";
    }

    /// <summary>
    /// True when an INSTALL ended because the app is already there, which is not a failure. winget turns an
    /// install of an installed package into an upgrade, and that upgrade ends with UPDATE_NOT_APPLICABLE when
    /// nothing newer applies to this PC; the Bulk Installer used to count that as "Failed" (#2462). The other two
    /// are winget's and the installer's own "already installed" results.
    /// </summary>
    public static bool IsAlreadyInstalled(int exitCode) =>
        exitCode is WingetExitCodes.UpdateNotApplicable
            or WingetExitCodes.PackageAlreadyInstalled
            or WingetExitCodes.InstallAlreadyInstalled;

    /// <summary>
    /// Explains why an UNINSTALL failed. Kept distinct from the install map on purpose: 1602 means
    /// "cancelled" for both, but most other codes do not correspond.
    /// </summary>
    /// <remarks>
    /// The numbered codes are the ones an uninstaller returns itself, on the path that runs it directly. Through
    /// winget, a failed uninstaller ends as EXEC_UNINSTALL_COMMAND_FAILED instead, and its own code is only
    /// printed — so without that entry every winget failure fell through to a signed decimal (#2462).
    /// </remarks>
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
            WingetExitCodes.ExecUninstallCommandFailed => "The app's uninstaller reported an error.",
            WingetExitCodes.NoUninstallInfoFound =>
                "Windows has no uninstall information for this app — remove it from Windows Settings ▸ Apps.",
            WingetExitCodes.NoApplicationsFound => "winget no longer finds this app installed (already removed?).",
            _ when WingetExitCodes.IsWingetCode(exitCode) => $"winget stopped with code {WingetExitCodes.Hex(exitCode)}.",
            _ => $"The app's uninstaller returned exit code {exitCode}.",
        };

        return $"Failed — {reason}";
    }
}
