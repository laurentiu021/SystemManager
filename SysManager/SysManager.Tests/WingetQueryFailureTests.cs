// SysManager · WingetQueryFailureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A winget query that FAILED must not read as one that found nothing (#2461).
/// </summary>
/// <remarks>
/// Four queries parse the table winget prints: the upgrade list (App Updates, and the Dashboard alert),
/// <c>winget list</c> twice (Uninstaller, and the Bulk Installer's "installed" marks) and <c>winget search</c>.
/// None read the exit code, so a query that failed printed no table and parsed as an empty one — "All
/// detected packages are up to date", "Found 0 installed applications", "No packages found".
/// <para>The exit codes are winget's own, measured on a real run where they could be: a failed query ended
/// <c>0x8A150012</c> with no table, a search for a name that exists nowhere ended <c>0x8A150014</c>
/// (NO_APPLICATIONS_FOUND), and a healthy upgrade list ended 0 with its table. The fixtures below are
/// written as those numbers rather than through the app's constants, so they check the constants too.</para>
/// </remarks>
public class WingetQueryFailureTests
{
    private const int FailedToOpenAllSources = unchecked((int)0x8A15004B); // FAILED_TO_OPEN_ALL_SOURCES
    private const int SourceNameDoesNotExist = unchecked((int)0x8A150012); // SOURCE_NAME_DOES_NOT_EXIST, measured
    private const int NothingMatched = unchecked((int)0x8A150014);         // NO_APPLICATIONS_FOUND, measured

    /// <summary>A runner that prints <paramref name="lines"/> as winget output, then exits with the code.</summary>
    private static IPowerShellRunner WingetThatPrints(int exitCode, params string[] lines)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(_ =>
              {
                  foreach (var line in lines)
                      runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(line));
                  return Task.FromResult(exitCode);
              });
        return runner;
    }

    private static readonly string[] UpgradeTable =
    [
        "Name                         Id                             Version   Available   Source",
        "-----------------------------------------------------------------------------------------",
        "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget",
        "1 upgrades available.",
    ];

    private static readonly string[] ListTable =
    [
        "Name                         Id                           Version    Source",
        "-------------------------------------------------------------------------",
        "Git                          Git.Git                      2.45.1     winget",
        "1 packages installed.",
    ];

    // ---------- App Updates: the upgrade list ----------

    [Fact]
    public async Task UpgradeList_WhenTheQueryFails_ThrowsInsteadOfReportingNoUpgrades()
    {
        var winget = new WingetService(WingetThatPrints(FailedToOpenAllSources, "Failed when opening source(s)."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => winget.ListUpgradableAsync());

        Assert.Contains("package sources", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeList_WhenNothingMatched_IsAnEmptyListNotAFailure()
    {
        // winget's own "nothing found" is a non-zero exit, so treating every non-zero as a failure would turn a
        // PC with nothing to upgrade into an error.
        var winget = new WingetService(WingetThatPrints(NothingMatched, "No installed package found matching input criteria."));

        Assert.Empty(await winget.ListUpgradableAsync());
    }

    [Fact]
    public async Task UpgradeList_WhenTheQueryWorks_StillParsesTheTable()
    {
        var winget = new WingetService(WingetThatPrints(0, UpgradeTable));

        var package = Assert.Single(await winget.ListUpgradableAsync());
        Assert.Equal("Microsoft.PowerShell", package.Id);
        Assert.Equal("7.4.4.0", package.AvailableVersion);
    }

    // ---------- Uninstaller: winget list ----------

    [Fact]
    public async Task InstalledList_WhenTheQueryFails_ThrowsInsteadOfReportingNothingInstalled()
    {
        var service = new UninstallerService(WingetThatPrints(SourceNameDoesNotExist), () => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstalledAsync());

        // The fallback names the code in the form winget prints it, never as a signed decimal.
        Assert.Contains("0x8A150012", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-1978", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledList_WhenNothingMatched_IsAnEmptyList()
    {
        var service = new UninstallerService(WingetThatPrints(NothingMatched), () => false);

        Assert.Empty(await service.ListInstalledAsync());
    }

    [Fact]
    public async Task InstalledList_WhenTheQueryWorks_StillParsesTheTable()
    {
        var service = new UninstallerService(WingetThatPrints(0, ListTable), () => false);

        Assert.Equal("Git.Git", Assert.Single(await service.ListInstalledAsync()).Id);
    }

    // ---------- Bulk Installer: winget search and winget list ----------

    [Fact]
    public async Task Search_WhenTheSearchFails_ThrowsInsteadOfMatchingNothing()
    {
        var service = new BulkInstallerService(WingetThatPrints(FailedToOpenAllSources));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("firefox"));
    }

    [Fact]
    public async Task Search_ThatMatchesNothing_ReturnsNormally()
    {
        // Measured: `winget search` for a name that exists nowhere exits NO_APPLICATIONS_FOUND. That is the
        // ordinary no-results case, and the tab has its own empty state for it.
        var service = new BulkInstallerService(WingetThatPrints(NothingMatched, "No package found matching input criteria."));

        var lines = await service.SearchAsync("qwertyasdfzxcv");

        Assert.Equal(new[] { "No package found matching input criteria." }, lines);
    }

    [Fact]
    public async Task InstalledMarks_WhenTheQueryFails_Throw()
    {
        var service = new BulkInstallerService(WingetThatPrints(FailedToOpenAllSources));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstalledAsync());
    }

    // ---------- the rule itself ----------

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x8A150014))] // NO_APPLICATIONS_FOUND
    public void AnExitThatListedSomething_OrNothing_IsNotAFailure(int exitCode)
        => Assert.Null(Record.Exception(() => WingetFailure.ThrowIfQueryFailed(exitCode)));

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0x8A150001))] // INTERNAL_ERROR
    [InlineData(unchecked((int)0x8A15004B))] // FAILED_TO_OPEN_ALL_SOURCES
    [InlineData(unchecked((int)0x8A150015))] // NO_SOURCES_DEFINED
    public void AnyOtherExit_IsAFailedQuery(int exitCode)
        => Assert.Throws<InvalidOperationException>(() => WingetFailure.ThrowIfQueryFailed(exitCode));

    [Fact]
    public void TheReasonIsPlainLanguage_ForTheFailuresAUserCanActOn()
    {
        Assert.Contains("internet connection", WingetFailure.DescribeQueryFailure(FailedToOpenAllSources),
            StringComparison.Ordinal);
        Assert.Contains("no package sources", WingetFailure.DescribeQueryFailure(unchecked((int)0x8A150015)),
            StringComparison.Ordinal);
        Assert.Equal(WingetExitCodes.FailedToOpenAllSources, FailedToOpenAllSources);
    }
}
