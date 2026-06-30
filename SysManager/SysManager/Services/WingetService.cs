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
public sealed partial class WingetService
{
    private readonly PowerShellRunner _runner;

    public WingetService(PowerShellRunner runner) => _runner = runner;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Runs 'winget upgrade' and parses the table output into <see cref="AppPackage"/>.
    /// </summary>
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

        _runner.LineReceived += Collect;
        try
        {
            await _runner.RunProcessAsync("winget",
                "upgrade --include-unknown --accept-source-agreements --disable-interactivity", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        return ParseUpgradeTable(captured.ToList());
    }

    private static List<AppPackage> ParseUpgradeTable(List<string> lines)
    {
        var rows = Helpers.WingetTableParser.Parse(lines, UpgradeHeaderPattern(), UpgradeSummaryPattern());
        var packages = new List<AppPackage>(rows.Count);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Available)) continue;

            packages.Add(new AppPackage
            {
                Name = row.Name,
                Id = row.Id,
                CurrentVersion = row.Version,
                AvailableVersion = row.Available,
                Source = string.IsNullOrWhiteSpace(row.Source) ? "winget" : row.Source,
                Status = "Pending"
            });
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

    [GeneratedRegex(@"^\d+\s+(upgrades|packages|package)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeSummaryPattern();

    public async Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default)
    {
        var args = "upgrade --all --silent --no-progress --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown";
        int code = await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
        return WingetResult.From(code);
    }
}
