// SysManager · UninstallerDescribeFailureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerViewModel.DescribeUninstallFailure"/>.
/// Verifies that every known exit code maps to a human-readable message
/// and that unknown codes produce a generic but informative fallback.
/// </summary>
public class UninstallerDescribeFailureTests
{
    [Theory]
    [InlineData(1, "generic error")]
    [InlineData(2, "cancelled")]
    [InlineData(5, "Access denied")]
    [InlineData(87, "manual uninstall")]
    [InlineData(1602, "cancelled by the user")]
    [InlineData(1603, "fatal error")]
    [InlineData(1605, "not currently installed")]
    [InlineData(1618, "another installation")]
    public void KnownExitCode_ContainsExpectedPhrase(int exitCode, string expectedPhrase)
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(exitCode, "TestApp");
        Assert.Contains(expectedPhrase, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1603)]
    [InlineData(9999)]
    public void AllCodes_StartWithFailed(int exitCode)
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(exitCode, "TestApp");
        Assert.StartsWith("Failed", result);
    }

    [Fact]
    public void UnknownExitCode_IncludesCodeNumber()
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(42, "TestApp");
        Assert.Contains("42", result);
    }

    [Fact]
    public void ExitCode5_DoesNotRecommendRunningSysManagerAsAdministrator()
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(5, "TestApp");

        Assert.Contains("Windows UAC prompt", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SysManager as Administrator", result, StringComparison.OrdinalIgnoreCase);
    }
}
