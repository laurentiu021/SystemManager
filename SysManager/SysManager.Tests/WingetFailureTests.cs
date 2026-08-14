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
        // number in the row before this.
        var text = WingetFailure.DescribeInstallFailure(unchecked((int)0x8A150011));

        Assert.Contains("cancelled", text, StringComparison.OrdinalIgnoreCase);
    }

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
        foreach (var code in new[] { 5, 1602, 1603, 1618, 1619, 1620, 1638, 4242, 0 })
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
        var vmDir = FindViewModelsDirectory();
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

    /// <summary>The app project's ViewModels directory — .cs sources are not copied to the test output.</summary>
    private static string FindViewModelsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SysManager", "ViewModels");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SysManager ViewModels directory from " + AppContext.BaseDirectory);
    }
}
