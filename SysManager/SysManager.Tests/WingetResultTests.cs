// SysManager · WingetResultTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;

namespace SysManager.Tests;

public class WingetResultTests
{
    [Fact]
    public void From_Zero_IsSucceededAndUpdated()
    {
        var r = WingetResult.From(0);
        Assert.True(r.Succeeded);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Updated", r.FriendlyMessage);
    }

    [Fact]
    public void From_NonZero_IsNotSucceeded()
    {
        var r = WingetResult.From(unchecked((int)0x8A15002B)); // UPDATE_NOT_APPLICABLE
        Assert.False(r.Succeeded);
        Assert.Equal("No applicable update found", r.FriendlyMessage);
    }

    // Numbers as winget-cli's AppInstallerErrors.h defines them, typed here rather than read through the app's
    // constants, so a constant that drifts off its number fails this table instead of agreeing with itself.
    [Theory]
    [InlineData(0x8A15002Bu, "No applicable update found")]                                // UPDATE_NOT_APPLICABLE
    [InlineData(0x8A150109u, "Update installed — restart required")]                       // INSTALL_REBOOT_REQUIRED_TO_FINISH
    [InlineData(0x8A150102u, "Another install is in progress — try again shortly")]        // INSTALL_INSTALL_IN_PROGRESS
    [InlineData(0x8A150101u, "App is running — close it and retry")]                       // INSTALL_PACKAGE_IN_USE
    [InlineData(0x8A150111u, "App is running — close it and retry")]                       // INSTALL_PACKAGE_IN_USE_BY_APPLICATION
    [InlineData(0x8A15010Cu, "Cancelled")]                                                 // INSTALL_CANCELLED_BY_USER
    [InlineData(0x8A150011u, "Download didn't match what winget expected — not installed")] // INSTALLER_HASH_MISMATCH
    [InlineData(0x8A150019u, "Needs administrator — restart SysManager as administrator")] // COMMAND_REQUIRES_ADMIN
    [InlineData(0x8A150010u, "No installer for this PC")]                                  // NO_APPLICABLE_INSTALLER
    [InlineData(0x8A150049u, "Installer failed — see the log")]                            // MSI_INSTALL_FAILED
    [InlineData(0x8A150006u, "Installer failed — see the log")]                            // SHELLEXEC_INSTALL_FAILED
    [InlineData(0x8A15004Bu, "Network error reaching the source")]                         // FAILED_TO_OPEN_ALL_SOURCES
    [InlineData(0x8A15002Cu, "Some apps could not be updated")]                            // UPDATE_ALL_HAS_FAILURE
    public void Describe_KnownCodes_AreFriendly(uint code, string expected)
        => Assert.Equal(expected, WingetExitCodes.Describe(unchecked((int)code)));

    /// <summary>
    /// The seven sentences that sat on the wrong code (#2462) are gone from those codes.
    /// </summary>
    /// <remarks>
    /// The worst was the hash mismatch: winget refuses to run an installer that does not match its manifest,
    /// and the row said "No applicable update found", which reads as harmless.
    /// </remarks>
    [Theory]
    [InlineData(0x8A150011u, "No applicable update found")]       // INSTALLER_HASH_MISMATCH
    [InlineData(0x8A150019u, "Cancelled")]                        // COMMAND_REQUIRES_ADMIN
    [InlineData(0x8A150049u, "Another install is in progress")]   // MSI_INSTALL_FAILED
    [InlineData(0x8A15010Cu, "App is running")]                   // INSTALL_CANCELLED_BY_USER
    [InlineData(0x8A150056u, "Installer failed")]                 // INSTALLER_PROHIBITS_ELEVATION
    [InlineData(0x8A150010u, "catalog")]                          // NO_APPLICABLE_INSTALLER
    [InlineData(0x8A150047u, "Network error")]                    // CUSTOMHEADER_EXCEEDS_MAXLENGTH
    public void Describe_NoLongerGivesACodeItsNeighboursSentence(uint code, string oldSentence)
        => Assert.DoesNotContain(oldSentence, WingetExitCodes.Describe(unchecked((int)code)), StringComparison.Ordinal);

    [Fact]
    public void Describe_UnknownCode_FallsBackToHex_NotRawDecimal()
    {
        // An unmapped non-zero code must render as hex, never a bare signed decimal
        // like "exit -1978335189" (the thing issue #1130 complained about).
        var msg = WingetExitCodes.Describe(unchecked((int)0x8A150099));
        Assert.Equal("Failed (winget code 0x8A150099)", msg);
        Assert.DoesNotContain("-", msg);
    }

    [Fact]
    public void Cancelled_IsNotSucceeded()
    {
        Assert.False(WingetResult.Cancelled.Succeeded);
        Assert.Equal("Cancelled", WingetResult.Cancelled.FriendlyMessage);
    }

    // ---------- the constants are winget's numbers ----------

    /// <summary>
    /// Every constant holds the number winget-cli gives the code of the same name.
    /// </summary>
    /// <remarks>
    /// The whole defect was a number standing for the wrong code, so the names are only worth anything if
    /// each one is pinned to its number from an independent source — this table, copied from winget-cli's
    /// <c>AppInstallerErrors.h</c>. A constant with no row here fails too, so a new one cannot skip the check.
    /// </remarks>
    [Fact]
    public void EveryConstant_HoldsWingetsNumberForItsName()
    {
        var header = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["ShellExecInstallFailed"] = 0x8A150006,
            ["DownloadFailed"] = 0x8A150008,
            ["NoApplicableInstaller"] = 0x8A150010,
            ["InstallerHashMismatch"] = 0x8A150011,
            ["NoApplicationsFound"] = 0x8A150014,
            ["NoSourcesDefined"] = 0x8A150015,
            ["CommandRequiresAdmin"] = 0x8A150019,
            ["UpdateNotApplicable"] = 0x8A15002B,
            ["UpdateAllHasFailure"] = 0x8A15002C,
            ["NoUninstallInfoFound"] = 0x8A15002F,
            ["ExecUninstallCommandFailed"] = 0x8A150030,
            ["SourceOpenFailed"] = 0x8A150045,
            ["MsiInstallFailed"] = 0x8A150049,
            ["FailedToOpenAllSources"] = 0x8A15004B,
            ["InstallerProhibitsElevation"] = 0x8A150056,
            ["PackageAlreadyInstalled"] = 0x8A150061,
            ["InstallPackageInUse"] = 0x8A150101,
            ["InstallInstallInProgress"] = 0x8A150102,
            ["InstallFileInUse"] = 0x8A150103,
            ["InstallDiskFull"] = 0x8A150105,
            ["InstallNoNetwork"] = 0x8A150107,
            ["InstallRebootRequiredToFinish"] = 0x8A150109,
            ["InstallRebootRequiredForInstall"] = 0x8A15010A,
            ["InstallCancelledByUser"] = 0x8A15010C,
            ["InstallAlreadyInstalled"] = 0x8A15010D,
            ["InstallBlockedByPolicy"] = 0x8A15010F,
            ["InstallPackageInUseByApplication"] = 0x8A150111,
        };

        var constants = typeof(WingetExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => unchecked((uint)(int)f.GetRawConstantValue()!), StringComparer.Ordinal);

        Assert.Equal(header.Count, constants.Count);
        foreach (var (name, value) in constants)
        {
            Assert.True(header.TryGetValue(name, out var expected), $"{name} has no row in this table");
            Assert.True(expected == value, $"{name} is 0x{value:X8}; winget defines it as 0x{expected:X8}");
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150011), true)]
    [InlineData(unchecked((int)0x8A150101), true)]
    [InlineData(0, false)]
    [InlineData(1603, false)]
    [InlineData(unchecked((int)0x80070005), false)] // E_ACCESSDENIED: an HRESULT, but not winget's
    public void IsWingetCode_RecognisesOnlyWingetsOwnRange(int exitCode, bool expected)
        => Assert.Equal(expected, WingetExitCodes.IsWingetCode(exitCode));
}
