// SysManager · WingetFailureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="WingetFailure"/> — the shared plain-language translation of winget outcomes.
/// <para>The defect this closes: three tabs run winget and had drifted into three levels of care.
/// Uninstaller translated its exit codes; App Updates reused Uninstaller's missing-winget sentence;
/// Bulk Installer did neither, writing "Failed (exit 1618)" and raw OS exception text into the row.
/// The same underlying failure was explained on two tabs and shown as a number on the third.</para>
/// </summary>
public class WingetFailureTests
{
    // ---------- install-side codes the user can act on ----------

    [Theory]
    [InlineData(1618, "Another installation")]     // the example from the issue
    [InlineData(5, "Access denied")]
    [InlineData(1602, "cancelled")]
    [InlineData(1603, "fatal error")]
    [InlineData(1619, "corrupt")]
    [InlineData(1638, "already installed")]
    public void DescribeInstallFailure_ExplainsAKnownCode(int exitCode, string expected)
    {
        var text = WingetFailure.DescribeInstallFailure(exitCode);

        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        // Never just the number: that is the whole point of the change.
        Assert.DoesNotContain($"exit {exitCode}", text);
    }

    [Fact]
    public void DescribeInstallFailure_TranslatesWingetsOwnCancelledResult()
    {
        // winget reports its own results as large unsigned values, which surfaced as a huge negative
        // number in the row before this. INSTALL_CANCELLED_BY_USER is winget's cancellation; this test used
        // to pin 0x8A150011 as "cancelled", which is INSTALLER_HASH_MISMATCH (#2462).
        var text = WingetFailure.DescribeInstallFailure(unchecked((int)0x8A15010C)); // INSTALL_CANCELLED_BY_USER

        Assert.Contains("cancelled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeInstallFailure_CallsAHashMismatchWhatItIs()
    {
        // winget refused to run an installer that does not match its manifest. "Cancelled" suggested the user
        // had done it, and said nothing about the download.
        var text = WingetFailure.DescribeInstallFailure(unchecked((int)0x8A150011)); // INSTALLER_HASH_MISMATCH

        Assert.Contains("did not match", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150006))] // SHELLEXEC_INSTALL_FAILED: how winget ends when an installer fails
    [InlineData(unchecked((int)0x8A150049))] // MSI_INSTALL_FAILED
    public void DescribeInstallFailure_ExplainsTheCodesWingetEndsAFailedInstallerOn(int exitCode)
        => Assert.Equal("Failed — The app's own installer reported an error.", WingetFailure.DescribeInstallFailure(exitCode));

    [Fact]
    public void DescribeInstallFailure_AnUnmappedWingetCodeIsShownInHex()
    {
        // The fallback printed winget's codes as signed decimals — "The installer returned code -1978335079."
        var text = WingetFailure.DescribeInstallFailure(unchecked((int)0x8A150099));

        Assert.Contains("0x8A150099", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-1978", text, StringComparison.Ordinal);
    }

    // ---------- an app that was already installed is not a failure ----------

    [Theory]
    [InlineData(unchecked((int)0x8A15002B), true)]  // UPDATE_NOT_APPLICABLE: an install turned into an upgrade, nothing newer
    [InlineData(unchecked((int)0x8A150061), true)]  // PACKAGE_ALREADY_INSTALLED
    [InlineData(unchecked((int)0x8A15010D), true)]  // INSTALL_ALREADY_INSTALLED
    [InlineData(0, false)]
    [InlineData(unchecked((int)0x8A150011), false)] // INSTALLER_HASH_MISMATCH
    [InlineData(1638, false)]                       // MSI: ANOTHER version is installed, which is a real conflict
    public void IsAlreadyInstalled_RecognisesOnlyTheAlreadyInstalledResults(int exitCode, bool expected)
        => Assert.Equal(expected, WingetFailure.IsAlreadyInstalled(exitCode));

    [Fact]
    public void DescribeInstallFailure_AnUnknownCodeStillReportsTheNumber()
    {
        // A code nobody has mapped must still be diagnosable — the fallback is a readable sentence
        // that happens to carry the number, not a bare number.
        var text = WingetFailure.DescribeInstallFailure(4242);

        Assert.Contains("4242", text);
        Assert.StartsWith("Failed —", text);
    }

    [Fact]
    public void DescribeInstallFailure_AlwaysReadsAsASentence()
    {
        // Every branch, including the fallback, must produce something a non-technical reader parses.
        foreach (var code in new[] { 5, 1602, 1603, 1618, 1619, 1620, 1638, 4242, 0,
                     unchecked((int)0x8A150011), unchecked((int)0x8A150099) })
        {
            var text = WingetFailure.DescribeInstallFailure(code);
            Assert.StartsWith("Failed —", text);
            Assert.EndsWith(".", text);
        }
    }

    // ---------- install and uninstall maps stay distinct ----------

    [Fact]
    public void InstallAndUninstallDoNotShareOneMap()
    {
        // 1605 means "not currently installed" for an UNINSTALL and nothing for an install; 1638 is
        // the reverse. Sharing one map would produce confidently wrong sentences, so the two are
        // deliberately separate — asserted so a later "simplification" cannot merge them.
        Assert.Contains("not currently installed", WingetFailure.DescribeUninstallFailure(1605));
        Assert.DoesNotContain("not currently installed", WingetFailure.DescribeInstallFailure(1605));

        Assert.Contains("already installed", WingetFailure.DescribeInstallFailure(1638));
        Assert.DoesNotContain("already installed", WingetFailure.DescribeUninstallFailure(1638));
    }

    [Theory]
    [InlineData(5, "Access denied")]
    [InlineData(1602, "cancelled")]
    [InlineData(1618, "Another installation")]
    public void DescribeUninstallFailure_StillExplainsWhatItAlwaysDid(int exitCode, string expected)
    {
        // The mapping moved into this helper; the behaviour must be unchanged.
        Assert.Contains(expected, WingetFailure.DescribeUninstallFailure(exitCode),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeUninstallFailure_ExplainsWingetsOwnUninstallFailure()
    {
        // Through winget, a failed uninstaller ends as EXEC_UNINSTALL_COMMAND_FAILED and its own code is only
        // printed. That code had no entry, so the row read "returned exit code -1978335184." (#2462).
        var text = WingetFailure.DescribeUninstallFailure(unchecked((int)0x8A150030)); // EXEC_UNINSTALL_COMMAND_FAILED

        Assert.Equal("Failed — The app's uninstaller reported an error.", text);
    }

    [Fact]
    public void DescribeUninstallFailure_AnUnmappedWingetCodeIsShownInHex()
    {
        var text = WingetFailure.DescribeUninstallFailure(unchecked((int)0x8A150099));

        Assert.Contains("0x8A150099", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-1978", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUninstallerViewModelNowDelegatesToTheSharedMap()
    {
        // Proves there is ONE source of truth rather than two copies that agree today. If the VM kept
        // a private copy, an edit to the helper would silently not reach the tab.
        foreach (var code in new[] { 1, 2, 5, 87, 1602, 1603, 1605, 1618, 9999 })
        {
            Assert.Equal(
                WingetFailure.DescribeUninstallFailure(code),
                UninstallerViewModel.DescribeUninstallFailure(code, "AnyApp"));
        }
    }

    // ---------- the missing-winget sentence ----------

    [Fact]
    public void AllThreeWingetTabsUseTheSameMissingWingetSentence()
    {
        // The literal used to live on AppUpdatesViewModel and be referenced cross-VM, which is exactly
        // how Bulk Installer ended up not using it at all. The name promises all three tabs, so all
        // three are read: asserting only the AppUpdates alias left the other two tabs free to
        // reintroduce their own wording — the very drift this test is named for.
        var vmDir = TestPaths.AppDir("ViewModels");
        var offenders = new List<string>();

        foreach (var vm in new[] { "AppUpdatesViewModel.cs", "UninstallerViewModel.cs", "BulkInstallerViewModel.cs" })
        {
            var source = File.ReadAllText(Path.Combine(vmDir, vm));

            // Each tab must reach the shared constant — directly, or through the AppUpdates alias that
            // forwards to it. A tab spelling the sentence itself would satisfy neither.
            if (!source.Contains("WingetFailure.WingetUnavailable", StringComparison.Ordinal)
                && !source.Contains("WingetUnavailableMessage", StringComparison.Ordinal))
                offenders.Add($"{vm} does not use the shared missing-winget sentence");
        }

        Assert.True(offenders.Count == 0,
            "Every winget tab must show the SAME sentence when winget is missing, so the user reads one "
            + "explanation rather than three:\n  " + string.Join("\n  ", offenders));

        // And the alias really does forward to the shared constant rather than holding a second copy.
        Assert.Equal(WingetFailure.WingetUnavailable, AppUpdatesViewModel.WingetUnavailableMessage);
    }

    [Fact]
    public void TheMissingWingetSentenceNamesWhatToInstall()
    {
        // "winget is not available" would leave the persona stuck; it has to name App Installer and
        // where to get it.
        Assert.Contains("App Installer", WingetFailure.WingetUnavailable);
        Assert.Contains("Microsoft Store", WingetFailure.WingetUnavailable);
    }

    [Fact]
    public void TheMissingWingetSentenceDoesNotReadLikeACrash()
    {
        // The point of the shared string: the tab needs a prerequisite, nothing broke.
        Assert.DoesNotContain("error", WingetFailure.WingetUnavailable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", WingetFailure.WingetUnavailable, StringComparison.OrdinalIgnoreCase);
    }
}
