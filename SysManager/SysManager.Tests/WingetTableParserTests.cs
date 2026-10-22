// SysManager · WingetTableParserTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Helpers;

namespace SysManager.Tests;

public class WingetTableParserTests
{
    private static readonly Regex HeaderPattern = new(@"^\s*Name\s+Id\s+Version", RegexOptions.IgnoreCase);
    private static readonly Regex SummaryPattern = new(@"^\d+\s+packages?\s+", RegexOptions.IgnoreCase);

    [Fact]
    public void Parse_EmptyLines_ReturnsEmpty()
    {
        var lines = new List<string>();
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NoHeader_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "Some random output",
            "No applicable upgrades found.",
            "Another line without headers"
        };
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------"
        };
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ValidTable_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Equal(3, result.Count);

        Assert.Equal("Git", result[0].Name);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.47.0", result[0].Version);
        Assert.Equal("2.48.0", result[0].Available);
        Assert.Equal("winget", result[0].Source);

        Assert.Equal("PowerShell", result[1].Name);
        Assert.Equal("Microsoft.PowerShell", result[1].Id);

        Assert.Equal("Node.js", result[2].Name);
        Assert.Equal("OpenJS.NodeJS", result[2].Id);
    }

    [Fact]
    public void Parse_DropsTheRowWithABlankId_AndKeepsTheRest()
    {
        // The guard two features silently depend on, and it had no test of its own.
        // Uninstaller carries the user's ticks across a rescan keyed on (Id, Name); Name alone
        // is known to repeat — UninstallerService.EnrichFromRegistry says so by using
        // TryAdd(app.Name, app) rather than the indexer. That key is only unique because a
        // blank Id never reaches it, which is decided HERE rather than at the call site, so
        // relaxing this line would silently degrade the key to ("", Name) and let one row's
        // tick apply to another. Dropping is also the only honest option downstream:
        // UninstallerService.UninstallAsync validates with WingetId.IsValid and throws, so a
        // row with no Id could be listed but never acted on.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Mystery App                                                 1.0.0     1.0.1       winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        // Selectively: the blank-Id row goes, its neighbours stay. Asserting only "not 3"
        // would also pass if the parser returned nothing at all.
        Assert.Equal(2, result.Count);
        Assert.Equal(["Git.Git", "OpenJS.NodeJS"], result.Select(r => r.Id));
        Assert.DoesNotContain("Mystery App", result.Select(r => r.Name));
    }

    [Fact]
    public void Parse_ShortLines_SkipsGracefully()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "ab",
            "x",
            "Git                          Git.Git                        2.47.0    2.48.0      winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }

    [Fact]
    public void Parse_StopsAtSummaryLine()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "3 packages have version numbers that cannot be determined.",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        // Should stop at the summary line and not include PowerShell
        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }

    // ── Locale independence ───────────────────────────────────────────────
    // Regression for the CRIT bug: on a non-English Windows, winget translates the column
    // TITLES (only the words — the order and the dashes separator are identical), so the old
    // English `Name\s+Id\s+Version` header regex never matched and the App Updates / Uninstaller
    // / Bulk Installer tabs returned empty. The parser now locates the header via the dashes
    // separator and maps columns positionally, so these headers must parse without any
    // language-specific pattern. The caller pattern below deliberately does NOT match, forcing
    // the locale-agnostic fallback path.

    private static readonly Regex NeverMatches = new(@"(?!)");

    [Fact]
    public void Parse_GermanHeader_ParsesViaSeparatorFallback()
    {
        // de-DE: "Name Kennung Version Verfügbar Quelle" — the English regex can't match.
        var lines = new List<string>
        {
            "Name                         Kennung                        Version   Verfügbar   Quelle",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Equal(2, result.Count);
        Assert.Equal("Git", result[0].Name);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.47.0", result[0].Version);
        Assert.Equal("2.48.0", result[0].Available);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("Microsoft.PowerShell", result[1].Id);
    }

    [Fact]
    public void Parse_FrenchHeader_ParsesViaSeparatorFallback()
    {
        // fr-FR: "Nom Id Version Disponible Source".
        var lines = new List<string>
        {
            "Nom                          Id                             Version   Disponible  Source",
            "-----------------------------------------------------------------------------------------",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Node.js", result[0].Name);
        Assert.Equal("OpenJS.NodeJS", result[0].Id);
        Assert.Equal("20.11.0", result[0].Version);
        Assert.Equal("22.1.0", result[0].Available);
    }

    [Fact]
    public void Parse_EnglishHeader_StillParses_WhenPatternDoesNotMatch()
    {
        // Even the English table must parse through the separator fallback alone, proving the
        // positional column mapping is not secretly relying on the English header regex.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.48.0", result[0].Available);
    }

    [Fact]
    public void Parse_NoHeaderAndNoSeparator_ReturnsEmpty()
    {
        // Neither the pattern nor a dashes row → nothing to anchor on → empty (no crash).
        var lines = new List<string> { "irgendeine Ausgabe", "keine Upgrades gefunden" };
        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_FourColumnListTable_MapsSourceNotAvailable()
    {
        // winget "list" has NO Available column: Name / Id / Version / Source (4 columns).
        // The 4th token is Source, not Available — mapping it to Available (a 5-column
        // assumption) would leave Source empty. Positional mapping must treat the last
        // column as Source when there are only 4.
        var lines = new List<string>
        {
            "Name                         Id                           Version    Source",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("1.90.0", result[0].Version);
        Assert.Equal("winget", result[0].Source);   // must not be empty
        Assert.Equal("", result[0].Available);       // no Available column in a list table
    }

    [Fact]
    public void Parse_GermanFourColumnList_MapsSourceViaSeparatorFallback()
    {
        // de-DE "winget list" with the localized 4-column header (no Available) and the
        // English pattern forced off — the exact non-English list-tab scenario.
        var lines = new List<string>
        {
            "Name                         Kennung                      Version    Quelle",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("", result[0].Available);
    }

    // ── F03 regression: unlisted locale, 5-column upgrade table ──────────────
    // The German/French tests above pass because those locales ARE in the title lists.
    // The real gap: a locale whose Available/Source WORDS we don't know. Id/Version fell
    // back to position, but Available/Source stayed -1 → every row's Available came back
    // blank → WingetService dropped every row → App Updates showed ZERO upgrades. For the
    // UNAMBIGUOUS 5-column shape we now map Available/Source positionally.

    [Fact]
    public void Parse_UnlistedLocale_FiveColumnUpgrade_MapsAvailableAndSourcePositionally()
    {
        // A fabricated locale whose column words are in NONE of the title lists. Only the
        // Name/Id/Version/Available/Source ORDER (and the dashes separator) are relied on.
        var lines = new List<string>
        {
            "Naimŭ                        Ident                        Verzia    Dostupny    Zdroj",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Equal(2, result.Count);
        // The whole point: Available is populated (not blank), so these upgrades are NOT dropped.
        Assert.Equal("2.48.0", result[0].Available);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("22.1.0", result[1].Available);
    }

    [Fact]
    public void Parse_UnlistedLocale_FourColumn_DoesNotGuessAvailableOrSource()
    {
        // A 4-column table in an unlisted locale is genuinely ambiguous by position
        // (list=…/Source vs source-less upgrade=…/Available), so the positional fallback
        // deliberately only fires at >=5 columns and does NOT guess the ambiguous 4th column.
        // Id still resolves positionally so the row is not lost. (Version can bleed into the
        // trailing column here — that's pre-existing 4-column behavior this fix doesn't touch.)
        var lines = new List<string>
        {
            "Naimŭ                        Ident                        Verzia    Stĺpec4",
            "-------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
        // The key invariant: neither ambiguous column is falsely populated.
        Assert.Equal("", result[0].Available);
        Assert.Equal("", result[0].Source);
    }

    // ── A second table below the first (#2849) ──────────────────────────────
    // winget prints one under the upgrade list for the apps that have to be named one by one, below a heading and
    // its own header. When the summary line between them is not recognised, both of those lines were read as rows.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // with the blank line winget prints above the heading, which the runner drops
    public void ASecondTable_EndsTheFirst_AndItsHeadingAndHeaderAreNotRows(bool blankLineAboveTheHeading)
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget",
            "2 upgrades, in a wording no pattern here knows.",
        };
        if (blankLineAboveTheHeading) lines.Add("");
        lines.Add("The following packages have an upgrade available, but require explicit targeting for upgrade:");
        lines.Add("Name         Id           Version Available Source");
        lines.Add("--------------------------------------------------");
        lines.Add("Contoso Tool Contoso.Tool 1.0     1.1       winget");

        var result = WingetTableParser.Parse(lines, HeaderPattern, NeverMatches);

        Assert.Equal(["Git.Git", "OpenJS.NodeJS"], result.Select(r => r.Id));
    }

    [Fact]
    public void ASeparatorBeforeAnyRow_IsStillSkipped()
    {
        // Only a separator after rows starts another table.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, NeverMatches);

        Assert.Equal("Git.Git", Assert.Single(result).Id);
    }

    // ── Columns where winget wrote them, on every language it ships (#2793, #2955) ──────────────────
    // winget pads each field to its column measured in display columns, two for a Chinese, Japanese or Korean
    // character, and these tables are padded the same way, with each language's own titles from winget's string
    // tables. The second row's name is two wide characters, so every column after it starts two characters
    // earlier in the string than in the first row.

    public static TheoryData<string, string[]> UpgradeTables() => new()
    {
        {
            "en-US",
            [
                "Name Id             Version   Available Source",
                "----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21  winget",
            ]
        },
        {
            "de-DE",
            [
                "Name ID             Version   Verfügbar Quelle",
                "----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21  winget",
            ]
        },
        {
            "es-ES",
            [
                "Nombre Id             Versión   Disponible Origen",
                "-------------------------------------------------",
                "Git    Git.Git        2.47.0    2.48.0     winget",
                "微信   Tencent.WeChat 3.9.12.51 4.0.0.21   winget",
            ]
        },
        {
            "fr-FR",
            [
                "Nom  ID             Version   Disponible Source",
                "-----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0     winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21   winget",
            ]
        },
        {
            "it-IT",
            [
                "Nome Id             Versione  Disponibile Origine",
                "-------------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0      winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21    winget",
            ]
        },
        {
            "ja-JP",
            [
                "名前 ID             バージョン 利用可能 ソース",
                "----------------------------------------------",
                "Git  Git.Git        2.47.0     2.48.0   winget",
                "微信 Tencent.WeChat 3.9.12.51  4.0.0.21 winget",
            ]
        },
        {
            "ko-KR",
            [
                "이름 장치 ID        버전      사용 가능 원본",
                "----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21  winget",
            ]
        },
        {
            "pt-BR",
            [
                "Nome ID             Versão    Disponível Origem",
                "-----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0     winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21   winget",
            ]
        },
        {
            "ru-RU",
            [
                "Имя  ИД             Версия    Доступно Источник",
                "-----------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0   winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21 winget",
            ]
        },
        {
            "zh-CN",
            [
                "名称 ID             版本      可用     源",
                "---------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0   winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21 winget",
            ]
        },
        {
            "zh-TW",
            [
                "名稱 識別碼         版本      可用     來源",
                "---------------------------------------------",
                "Git  Git.Git        2.47.0    2.48.0   winget",
                "微信 Tencent.WeChat 3.9.12.51 4.0.0.21 winget",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(UpgradeTables))]
    public void AnUpgradeTable_InEachLanguage_IsReadColumnByColumn(string language, string[] table)
    {
        var rows = WingetTableParser.Parse([.. table], NeverMatches, NeverMatches);

        Assert.True(rows.Count == 2, language);
        Assert.Equal(("Git", "Git.Git", "2.47.0", "2.48.0", "winget"),
            (rows[0].Name, rows[0].Id, rows[0].Version, rows[0].Available, rows[0].Source));
        Assert.Equal(("微信", "Tencent.WeChat", "3.9.12.51", "4.0.0.21", "winget"),
            (rows[1].Name, rows[1].Id, rows[1].Version, rows[1].Available, rows[1].Source));
    }

    public static TheoryData<string, string[]> ListTables() => new()
    {
        {
            "en-US",
            [
                "Name Id             Version   Source",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "de-DE",
            [
                "Name ID             Version   Quelle",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "es-ES",
            [
                "Nombre Id             Versión   Origen",
                "--------------------------------------",
                "Git    Git.Git        2.47.0    winget",
                "微信   Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "fr-FR",
            [
                "Nom  ID             Version   Source",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "it-IT",
            [
                "Nome Id             Versione  Origine",
                "-------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "ja-JP",
            [
                "名前 ID             バージョン ソース",
                "-------------------------------------",
                "Git  Git.Git        2.47.0     winget",
                "微信 Tencent.WeChat 3.9.12.51  winget",
            ]
        },
        {
            "ko-KR",
            [
                "이름 장치 ID        버전      원본",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "pt-BR",
            [
                "Nome ID             Versão    Origem",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "ru-RU",
            [
                "Имя  ИД             Версия    Источник",
                "--------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "zh-CN",
            [
                "名称 ID             版本      源",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
        {
            "zh-TW",
            [
                "名稱 識別碼         版本      來源",
                "------------------------------------",
                "Git  Git.Git        2.47.0    winget",
                "微信 Tencent.WeChat 3.9.12.51 winget",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ListTables))]
    public void AListTable_InEachLanguage_FindsItsSourceColumn(string language, string[] table)
    {
        // A four-column list table has no positional fallback, so a Source title missing from the lists ran the
        // Version column on to the end of the line and left Source empty.
        var rows = WingetTableParser.Parse([.. table], NeverMatches, NeverMatches);

        Assert.True(rows.Count == 2, language);
        Assert.Equal(("Git.Git", "2.47.0", "", "winget"),
            (rows[0].Id, rows[0].Version, rows[0].Available, rows[0].Source));
        Assert.Equal(("Tencent.WeChat", "3.9.12.51", "", "winget"),
            (rows[1].Id, rows[1].Version, rows[1].Available, rows[1].Source));
    }

    [Fact]
    public void ARowShorterInCharactersThanInColumns_IsStillRead()
    {
        // Ten wide characters take twenty columns and ten characters, so this row reaches the Version column but its
        // string is shorter than the column's position, and a check by length dropped it as a stray line.
        var rows = WingetTableParser.Parse(
        [
            "Name                 Id  Version",
            "--------------------------------",
            "一二三四五六七八九十 A.B 1",
        ], NeverMatches, NeverMatches);

        Assert.Equal(("A.B", "1"), (Assert.Single(rows).Id, rows[0].Version));
    }
}
