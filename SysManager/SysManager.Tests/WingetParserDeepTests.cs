// SysManager · WingetParserDeepTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class WingetParserDeepTests
{
    private static List<AppPackage> Parse(List<string> lines)
    {
        var m = typeof(WingetService).GetMethod("ParseUpgradeTable", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<AppPackage>)m.Invoke(null, new object[] { lines })!;
    }

    [Fact]
    public void HandlesMixedCaseHeader()
    {
        var lines = new List<string>
        {
            "name                         id                             version   available   source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
        };
        var result = Parse(lines);
        // Header is case-insensitive.
        Assert.Single(result);
    }

    [Fact]
    public void HandlesPackageWithSpacesInName()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Visual Studio Build Tools    Microsoft.VCBuildTools         17.10.0   17.11.0     winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("Visual Studio Build Tools", result[0].Name);
        Assert.Equal("Microsoft.VCBuildTools", result[0].Id);
    }

    [Fact]
    public void HandlesMultiLineTableWithSpecialChars()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Python 3.12 (3.12.4)         Python.Python.3.12             3.12.4    3.12.5      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget",
        };
        var result = Parse(lines);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HandlesNoSourceColumn()
    {
        // Some winget versions omit the Source column.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available",
            "---------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("winget", result[0].Source);
    }

    [Fact]
    public void NoUpgradesAvailable_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "No applicable upgrades found.",
            "No installed package found matching input criteria.",
        };
        var result = Parse(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "  Padded Name                Padded.Id                      1.0.0     2.0.0       winget   ",
        };
        var result = Parse(lines);
        Assert.Equal("Padded Name", result[0].Name);
        Assert.Equal("Padded.Id", result[0].Id);
        Assert.Equal("winget", result[0].Source);
    }

    [Fact]
    public void Parse_IgnoresShortLines()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "abc",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }

    [Fact]
    public void Parse_StopsAtSingularPackageSummary()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "1 package has version numbers that cannot be determined.",
        };
        var result = Parse(lines);
        Assert.Single(result);
    }

    [Fact]
    public void Parse_HandlesAllEmptyInput_Safely()
    {
        var result = Parse(new List<string> { "" });
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_HandlesVeryLongId_WithoutTruncation()
    {
        var lines = new List<string>
        {
            "Name                         Id                                                                   Version   Available   Source",
            "----------------------------------------------------------------------------------------------------------------------------------",
            "Some App                     Super.Long.Package.Identifier.That.Goes.On.And.On                    1.0.0     2.0.0       winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Contains("Super.Long.Package", result[0].Id);
    }

    // ── An installed version winget cannot read ─────────────────────────────

    [Fact]
    public void ARowWithAnUnknownInstalledVersion_IsListedButNotTicked()
    {
        // winget lists such an app only with --include-unknown, and reinstalling it does not tell winget the version,
        // so it came back on every scan, ticked, and was reinstalled each time.
        var result = Parse(
        [
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Contoso Helper               Contoso.Helper                 Unknown   3.1.0       winget",
        ]);

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(p => p.Id == "Git.Git").IsSelected);
        var unknown = result.Single(p => p.Id == "Contoso.Helper");
        Assert.True(unknown.HasUnknownVersion);
        Assert.False(unknown.IsSelected);
    }

    // ---------- SysManager's own package (#2889) ----------
    // About updates SysManager. Listed on App Updates and counted by the Dashboard, its own row was offered beside
    // About saying it was up to date, and upgrading it had winget replace the file SysManager runs from.

    private static readonly string[] TableWithSysManager =
    [
        "Name                         Id                             Version   Available   Source",
        "-----------------------------------------------------------------------------------------",
        "Git                          Git.Git                        2.47.0    2.48.0      winget",
        "Contoso Helper               Contoso.Helper                 Unknown   3.1.0       winget",
        "SysManager                   laurentiu021.SysManager        1.130.20  1.130.27    winget",
        "3 upgrades available.",
    ];

    [Fact]
    public void SysManagersOwnPackage_IsNotListed()
    {
        var result = Parse([.. TableWithSysManager]);

        Assert.Equal(["Git.Git", "Contoso.Helper"], result.Select(p => p.Id));
    }

    /// <summary>
    /// A winget whose upgrade list prints <paramref name="table"/>, <see cref="TableWithSysManager"/> unless given
    /// another, and exits with <paramref name="listExit"/>, whose upgrades exit with <paramref name="upgradeExit"/>,
    /// and which records every command it was given.
    /// </summary>
    private static IPowerShellRunner Winget(List<string> commands, Func<string, int> upgradeExit,
        string[]? table = null, int listExit = 0)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
          .Returns(call =>
          {
              var args = call.ArgAt<string>(1);
              commands.Add(args);
              if (!args.StartsWith("upgrade --include-unknown", StringComparison.Ordinal))
                  return Task.FromResult(upgradeExit(args));
              foreach (var line in table ?? TableWithSysManager)
                  ps.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(line));
              return Task.FromResult(listExit);
          });
        return ps;
    }

    [Fact]
    public async Task UpdateAll_UpgradesEachListedApp_ButNeitherSysManagerNorOneOfUnknownVersion()
    {
        // Not upgrade --all, which takes SysManager's own package too. An app whose installed version winget cannot
        // read stays out, as upgrade --all leaves it: it was reinstalled on every press.
        var commands = new List<string>();

        var result = await new WingetService(Winget(commands, _ => 0)).UpgradeAllAsync();

        Assert.True(result.Succeeded);
        var upgrades = commands.Skip(1).ToList();
        var upgrade = Assert.Single(upgrades);
        Assert.StartsWith("upgrade --id \"Git.Git\" -e ", upgrade, StringComparison.Ordinal);
        Assert.DoesNotContain(commands, c => c.Contains("--all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAll_WhenAnAppFails_FailsWithTheCodeUpgradeAllEndsOn()
    {
        var commands = new List<string>();

        var result = await new WingetService(Winget(commands, _ => WingetExitCodes.InstallerHashMismatch)).UpgradeAllAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(WingetExitCodes.UpdateAllHasFailure, result.ExitCode);
    }

    [Fact]
    public async Task UpdateAll_WhenTheUpgradeListCannotBeRead_UpgradesNothingAndSaysWhy()
    {
        // The list is read before anything is upgraded, so a read that failed must end the run with its reason, never
        // as an empty list the run then reports as "All apps updated". The table is printed anyway: the exit code
        // decides.
        var commands = new List<string>();
        var winget = Winget(commands, _ => 0, listExit: WingetExitCodes.FailedToOpenAllSources);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new WingetService(winget).UpgradeAllAsync());

        Assert.Equal(WingetFailure.DescribeQueryFailure(WingetExitCodes.FailedToOpenAllSources), ex.Message);
        Assert.Single(commands);
    }

    [Fact]
    public async Task UpdateAll_AnAppWhoseIdWingetCutShort_CountsAsAFailure_AndTheOthersStillUpgrade()
    {
        // winget ends an id too long for its column with "…", and that is no id winget can be given back.
        string[] table =
        [
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Contoso Studio               Contoso.Studio.Professio…      4.0.1     4.2.0       winget",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "2 upgrades available.",
        ];
        var commands = new List<string>();

        var result = await new WingetService(Winget(commands, _ => 0, table)).UpgradeAllAsync();

        Assert.Equal(WingetExitCodes.UpdateAllHasFailure, result.ExitCode);
        var upgrade = Assert.Single(commands.Skip(1));
        Assert.StartsWith("upgrade --id \"Git.Git\" -e ", upgrade, StringComparison.Ordinal);
    }

    // ---------- the list ends at winget's summary line, in every language winget ships (#2849) ----------

    // winget's AvailableUpgrades line for two upgrades, as its string table for each language words it.
    [Theory]
    [InlineData("2 upgrades available.")]  // en-US
    [InlineData("2 Aktualisierungen verfügbar.")]  // de-DE
    [InlineData("2 actualizaciones disponibles.")]  // es-ES
    [InlineData("2 mises à niveau disponibles.")]  // fr-FR
    [InlineData("2 aggiornamenti disponibili.")]  // it-IT
    [InlineData("2 アップグレードを利用できます。")]  // ja-JP
    [InlineData("2 업그레이드를 사용할 수 있습니다.")]  // ko-KR
    [InlineData("2 atualizações disponíveis.")]  // pt-BR
    [InlineData("Доступны обновления: 2.")]  // ru-RU
    [InlineData("2 升级可用。")]  // zh-CN
    [InlineData("2 個升級可供使用。")]  // zh-TW
    public void TheList_EndsAtWingetsSummaryLine_InEveryLanguageItShips(string summary)
    {
        // A package line after the summary would be listed as one more update if the summary did not end the list.
        var result = Parse(
        [
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget",
            summary,
            "Contoso Pinned               Contoso.Pinned                 1.0.0     1.1.0       winget",
        ]);

        Assert.Equal(["Git.Git", "OpenJS.NodeJS"], result.Select(p => p.Id));
    }

    [Fact]
    public void AGermanList_FollowedByTheListOfAppsToNameOneByOne_HoldsOnlyItsOwnApps()
    {
        // What a German winget prints when an app has to be named to be updated: the summary, a heading, and a
        // second list under its own header. The heading and that header were listed as apps, ticked.
        var result = Parse(
        [
            "Name                     ID                         Version     Verfügbar   Quelle",
            "----------------------------------------------------------------------------------",
            "Git                      Git.Git                    2.47.0      2.48.0      winget",
            "Mozilla Firefox (x64 de) Mozilla.Firefox.de         139.0       140.0       winget",
            "2 Aktualisierungen verfügbar.",
            "Für die folgenden Pakete ist ein Upgrade verfügbar, für das Upgrade ist jedoch eine explizite "
                + "Zielgruppenadressierung erforderlich:",
            "Name         ID           Version Verfügbar Quelle",
            "--------------------------------------------------",
            "Contoso Tool Contoso.Tool 1.0     1.1       winget",
        ]);

        Assert.Equal(["Git.Git", "Mozilla.Firefox.de"], result.Select(p => p.Id));
    }

    [Fact]
    public void ARussianList_EndsAtItsSummary_WhereTheCountComesLast()
    {
        var result = Parse(
        [
            "Имя                      ИД                         Версия      Доступно    Источник",
            "------------------------------------------------------------------------------------",
            "Git                      Git.Git                    2.47.0      2.48.0      winget",
            "Доступны обновления: 1.",
            "Для следующих пакетов доступно обновление, но для обновления требуется явный выбор целевого "
                + "объекта.",
            "Имя          ИД           Версия  Доступно Источник",
            "---------------------------------------------------",
            "Contoso Tool Contoso.Tool 1.0     1.1      winget",
        ]);

        Assert.Equal(["Git.Git"], result.Select(p => p.Id));
    }

    // ---------- an update that needs a restart to finish is not a failure (#2849) ----------

    [Fact]
    public async Task UpdateAll_WhenAnAppNeedsARestartToFinish_Succeeds_AndSaysARestartIsNeeded()
    {
        var commands = new List<string>();

        var result = await new WingetService(Winget(commands, _ => WingetExitCodes.InstallRebootRequiredToFinish))
            .UpgradeAllAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task UpdateAll_WhenOneAppFails_AndAnotherNeedsARestart_StillFails()
    {
        string[] table =
        [
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget",
            "2 upgrades available.",
        ];
        var commands = new List<string>();
        var winget = Winget(commands,
            args => args.Contains("Git.Git", StringComparison.Ordinal)
                ? WingetExitCodes.InstallRebootRequiredToFinish
                : WingetExitCodes.MsiInstallFailed,
            table);

        var result = await new WingetService(winget).UpgradeAllAsync();

        Assert.Equal(WingetExitCodes.UpdateAllHasFailure, result.ExitCode);
        Assert.False(result.Succeeded);
    }
}
