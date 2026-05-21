// SysManager · WingetService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
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
        List<string> captured = [];

        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            await _runner.RunProcessAsync("winget",
                "upgrade --include-unknown --accept-source-agreements --disable-interactivity", ct);
        }
        finally { _runner.LineReceived -= Collect; }

        return ParseUpgradeTable(captured);
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

    public async Task<int> UpgradeAsync(string packageId, CancellationToken ct = default)
    {
        // Validate packageId: allowlist alphanumeric, dots, hyphens, underscores,
        // forward slashes (scoped IDs), and plus signs (e.g. "Notepad++.Notepad++").
        if (string.IsNullOrWhiteSpace(packageId)
            || !PackageIdPattern().IsMatch(packageId))
            throw new ArgumentException("Invalid package ID.", nameof(packageId));

        var args = $"upgrade --id \"{packageId}\" -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches valid winget package IDs: alphanumeric, dots, hyphens,
    /// underscores, forward slashes, plus signs, and spaces. Max 256 chars.
    /// </summary>
    [GeneratedRegex(@"^[\w.\-/+\s]{1,256}$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^\s*Name\s+Id\s+Version\s+Available", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeHeaderPattern();

    [GeneratedRegex(@"^\d+\s+(upgrades|packages|package)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeSummaryPattern();

    public async Task<int> UpgradeAllAsync(CancellationToken ct = default)
    {
        var args = "upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }
}
