// SysManager · WingetService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to list upgradable packages and install updates with live streaming.
/// </summary>
public sealed partial class WingetService : IWingetService
{
    private readonly IPowerShellRunner _runner;

    public WingetService(IPowerShellRunner runner) => _runner = runner;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Runs 'winget upgrade' and parses the table output into <see cref="AppPackage"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query failed, as opposed to finding nothing.</exception>
    public async Task<List<AppPackage>> ListUpgradableAsync(CancellationToken ct = default)
    {
        // LineReceived fires from both the stdout and stderr reader threads
        // concurrently, so the sink must be thread-safe — a plain List<T>.Add can
        // corrupt the backing array or drop a line under the race.
        var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();

        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Enqueue(l.Text);
        }

        int exitCode;
        _runner.LineReceived += Collect;
        try
        {
            exitCode = await _runner.RunProcessAsync("winget",
                "upgrade --include-unknown --accept-source-agreements --disable-interactivity", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        // A failed query prints no table, and parsing nothing used to read as "no upgrades" (#2461).
        WingetFailure.ThrowIfQueryFailed(exitCode);
        return ParseUpgradeTable(captured.ToList());
    }

    private static List<AppPackage> ParseUpgradeTable(List<string> lines)
    {
        var rows = Helpers.WingetTableParser.Parse(lines, UpgradeHeaderPattern(), UpgradeSummaryPattern());
        var packages = new List<AppPackage>(rows.Count);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Available)) continue;

            // SysManager's own package: About updates SysManager. Listed here, it was offered beside About saying
            // SysManager was up to date, and upgrading it had winget replace the file SysManager runs from (#2889).
            if (string.Equals(row.Id, UpdateService.WingetPackageId, StringComparison.OrdinalIgnoreCase)) continue;

            var package = new AppPackage
            {
                Name = row.Name,
                Id = row.Id,
                CurrentVersion = row.Version,
                AvailableVersion = row.Available,
                Source = string.IsNullOrWhiteSpace(row.Source) ? "winget" : row.Source,
                Status = AppPackage.PendingStatus
            };
            // Listed, but not ticked: winget cannot tell whether what it offers is newer than what is installed.
            package.IsSelected = !package.HasUnknownVersion;
            packages.Add(package);
        }
        return packages;
    }

    public async Task<WingetResult> UpgradeAsync(string packageId, CancellationToken ct = default)
    {
        // Validate packageId before interpolating it into the winget command line.
        if (!WingetId.IsValid(packageId))
            throw new ArgumentException("Invalid package ID.", nameof(packageId));

        // --no-progress suppresses winget's animated block-character progress bar, which
        // otherwise streams as garbled glyphs into the live console.
        var args = $"upgrade --id \"{packageId}\" -e --silent --no-progress --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown";
        int code = await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
        return WingetResult.From(code);
    }

    [GeneratedRegex(@"^\s*Name\s+Id\s+Version\s+Available", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeHeaderPattern();

    // winget's summary line, "{0} upgrades available." (AvailableUpgrades in its string tables), in each language it
    // ships, which ends the table. Only the English one was known, so on any other language the table never ended and
    // the heading and header of the list winget prints below it were read as packages (#2849). "packages" and
    // "package" are older English wordings.
    [GeneratedRegex(@"^\s*(?:\d+\s+(?:upgrades|packages|package)\s+"
        + @"|\d+\s+Aktualisierungen\s+verfügbar\."          // de-DE
        + @"|\d+\s+actualizaciones\s+disponibles\."         // es-ES
        + @"|\d+\s+mises\s+à\s+niveau\s+disponibles\."      // fr-FR
        + @"|\d+\s+aggiornamenti\s+disponibili\."           // it-IT
        + @"|\d+\s+アップグレードを利用できます。"           // ja-JP
        + @"|\d+\s+업그레이드를\s+사용할\s+수\s+있습니다\."     // ko-KR
        + @"|\d+\s+atualizações\s+disponíveis\."            // pt-BR
        + @"|Доступны\s+обновления:\s+\d+\."                // ru-RU
        + @"|\d+\s+升级可用。"                               // zh-CN
        + @"|\d+\s+個升級可供使用。)",                        // zh-TW
        RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeSummaryPattern();

    /// <summary>Upgrades every app the upgrade list holds, one by one, and fails when any of them fails.</summary>
    /// <remarks>
    /// By id rather than <c>upgrade --all</c>, which took SysManager's own package too and had winget replace the file
    /// SysManager runs from (#2889). An app whose installed version winget cannot read is left out, as
    /// <c>upgrade --all</c> leaves it: it was reinstalled on every press, and the Dashboard's count leaves it out too.
    /// A failure is reported with the code <c>upgrade --all</c> ends on when one of its apps fails, and, when none
    /// failed, an app that needs Windows restarted to finish with the code winget ends that upgrade on.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The upgrade list could not be read, so nothing was upgraded.
    /// </exception>
    public async Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default)
    {
        var failed = false;
        var restart = false;
        foreach (var package in (await ListUpgradableAsync(ct).ConfigureAwait(false)).Where(p => !p.HasUnknownVersion))
        {
            if (!WingetId.IsValid(package.Id))
            {
                failed = true;
                continue;
            }

            var result = await UpgradeAsync(package.Id, ct).ConfigureAwait(false);
            failed |= !result.Succeeded;
            restart |= result.RestartRequired;
        }

        return WingetResult.From(failed ? WingetExitCodes.UpdateAllHasFailure
            : restart ? WingetExitCodes.InstallRebootRequiredToFinish : 0);
    }
}
