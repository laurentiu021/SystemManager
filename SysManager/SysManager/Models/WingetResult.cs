// SysManager · WingetResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The outcome of a winget operation, translated from the raw process exit code into a
/// human-readable result so the UI never has to surface a bare numeric code.
/// </summary>
public sealed record WingetResult(int ExitCode, bool Succeeded, string FriendlyMessage)
{
    public static WingetResult From(int exitCode) =>
        new(exitCode, exitCode == 0, WingetExitCodes.Describe(exitCode));

    /// <summary>A cancelled operation (no meaningful exit code).</summary>
    public static WingetResult Cancelled { get; } = new(-1, false, "Cancelled");
}

/// <summary>
/// winget's own result codes, and what each one means for an UPGRADE in short, friendly English.
/// Unknown non-zero codes fall back to a hex form. Pure and unit-testable.
/// </summary>
/// <remarks>
/// winget.exe exits with the HRESULT its workflow ended on, so these are the process exit codes. Each
/// constant carries winget-cli's own name for the value (<c>APPINSTALLER_CLI_ERROR_*</c>, in
/// <c>AppInstallerErrors.h</c>), and every other file uses the constant rather than the number. The map
/// used to be written as bare numbers, and seven of its nine entries had drifted onto a neighbouring
/// code's sentence (#2462): a hash mismatch read as "No applicable update found", a command that needed
/// administrator as "Cancelled", and an install the user cancelled as "App is running".
/// </remarks>
public static class WingetExitCodes
{
    public const int ShellExecInstallFailed = unchecked((int)0x8A150006);           // SHELLEXEC_INSTALL_FAILED
    public const int DownloadFailed = unchecked((int)0x8A150008);                   // DOWNLOAD_FAILED
    public const int NoApplicableInstaller = unchecked((int)0x8A150010);            // NO_APPLICABLE_INSTALLER
    public const int InstallerHashMismatch = unchecked((int)0x8A150011);            // INSTALLER_HASH_MISMATCH
    public const int NoApplicationsFound = unchecked((int)0x8A150014);              // NO_APPLICATIONS_FOUND
    public const int NoSourcesDefined = unchecked((int)0x8A150015);                 // NO_SOURCES_DEFINED
    public const int CommandRequiresAdmin = unchecked((int)0x8A150019);             // COMMAND_REQUIRES_ADMIN
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);              // UPDATE_NOT_APPLICABLE
    public const int UpdateAllHasFailure = unchecked((int)0x8A15002C);              // UPDATE_ALL_HAS_FAILURE
    public const int NoUninstallInfoFound = unchecked((int)0x8A15002F);             // NO_UNINSTALL_INFO_FOUND
    public const int ExecUninstallCommandFailed = unchecked((int)0x8A150030);       // EXEC_UNINSTALL_COMMAND_FAILED
    public const int SourceOpenFailed = unchecked((int)0x8A150045);                 // SOURCE_OPEN_FAILED
    public const int MsiInstallFailed = unchecked((int)0x8A150049);                 // MSI_INSTALL_FAILED
    public const int FailedToOpenAllSources = unchecked((int)0x8A15004B);           // FAILED_TO_OPEN_ALL_SOURCES
    public const int InstallerProhibitsElevation = unchecked((int)0x8A150056);      // INSTALLER_PROHIBITS_ELEVATION
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);          // PACKAGE_ALREADY_INSTALLED
    public const int InstallPackageInUse = unchecked((int)0x8A150101);              // INSTALL_PACKAGE_IN_USE
    public const int InstallInstallInProgress = unchecked((int)0x8A150102);         // INSTALL_INSTALL_IN_PROGRESS
    public const int InstallFileInUse = unchecked((int)0x8A150103);                 // INSTALL_FILE_IN_USE
    public const int InstallDiskFull = unchecked((int)0x8A150105);                  // INSTALL_DISK_FULL
    public const int InstallNoNetwork = unchecked((int)0x8A150107);                 // INSTALL_NO_NETWORK
    public const int InstallRebootRequiredToFinish = unchecked((int)0x8A150109);    // INSTALL_REBOOT_REQUIRED_TO_FINISH
    public const int InstallRebootRequiredForInstall = unchecked((int)0x8A15010A);  // INSTALL_REBOOT_REQUIRED_FOR_INSTALL
    public const int InstallCancelledByUser = unchecked((int)0x8A15010C);           // INSTALL_CANCELLED_BY_USER
    public const int InstallAlreadyInstalled = unchecked((int)0x8A15010D);          // INSTALL_ALREADY_INSTALLED
    public const int InstallBlockedByPolicy = unchecked((int)0x8A15010F);           // INSTALL_BLOCKED_BY_POLICY
    public const int InstallPackageInUseByApplication = unchecked((int)0x8A150111); // INSTALL_PACKAGE_IN_USE_BY_APPLICATION

    /// <summary>
    /// True for a code in winget's own range (facility <c>0xA15</c>), as opposed to an exit code an installer
    /// or uninstaller returned itself. A winget code is only readable in hex; as a signed decimal it is noise.
    /// </summary>
    public static bool IsWingetCode(int exitCode) => (unchecked((uint)exitCode) >> 16) == 0x8A15;

    /// <summary>The code in the form winget and its documentation print it, e.g. <c>0x8A150011</c>.</summary>
    public static string Hex(int exitCode) => $"0x{unchecked((uint)exitCode):X8}";

    public static string Describe(int exitCode) => exitCode switch
    {
        0 => "Updated",
        UpdateNotApplicable => "No applicable update found",
        InstallRebootRequiredToFinish => "Update installed — restart required",
        InstallRebootRequiredForInstall => "Restart Windows, then try again",
        InstallInstallInProgress => "Another install is in progress — try again shortly",
        InstallPackageInUse or InstallPackageInUseByApplication or InstallFileInUse => "App is running — close it and retry",
        InstallCancelledByUser => "Cancelled",
        ShellExecInstallFailed or MsiInstallFailed => "Installer failed — see the log",
        NoApplicationsFound => "No matching installed app found",
        SourceOpenFailed or FailedToOpenAllSources => "Network error reaching the source",
        DownloadFailed or InstallNoNetwork => "Download failed — check the connection and retry",
        InstallerHashMismatch => "Download didn't match what winget expected — not installed",
        NoApplicableInstaller => "No installer for this PC",
        InstallDiskFull => "Not enough disk space",
        InstallBlockedByPolicy => "Blocked by a system policy",
        CommandRequiresAdmin => "Needs administrator — restart SysManager as administrator",
        InstallerProhibitsElevation => "Can't update while SysManager runs as administrator",
        UpdateAllHasFailure => "Some apps could not be updated",
        _ => $"Failed (winget code {Hex(exitCode)})",
    };
}
