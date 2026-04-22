// SysManager · WingetService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to list upgradable packages and install updates with live streaming.
/// </summary>
public class WingetService
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
        var captured = new List<string>();

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
        var packages = new List<AppPackage>();
        // Find the header line containing "Name" and "Id" and "Version" and "Available"
        int headerIdx = lines.FindIndex(l =>
            Regex.IsMatch(l, @"^\s*Name\s+Id\s+Version\s+Available", RegexOptions.IgnoreCase));
        if (headerIdx < 0) return packages;

        var header = lines[headerIdx];
        int idxId = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        int idxVersion = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        int idxAvailable = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
        int idxSource = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);
        if (idxId < 0 || idxVersion < 0 || idxAvailable < 0) return packages;

        for (int i = headerIdx + 2; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("--")) continue;
            // Stop at "X upgrades available." summary or similar
            if (Regex.IsMatch(line, @"^\d+\s+(upgrades|packages|package)\s+", RegexOptions.IgnoreCase)) break;
            if (line.Length < idxAvailable) continue;

            string Slice(int start, int end) =>
                start < line.Length ? line[start..Math.Min(end, line.Length)].Trim() : string.Empty;

            var name = Slice(0, idxId);
            var id = Slice(idxId, idxVersion);
            var version = Slice(idxVersion, idxAvailable);
            var available = idxSource > 0 ? Slice(idxAvailable, idxSource) : Slice(idxAvailable, line.Length);
            var source = idxSource > 0 ? Slice(idxSource, line.Length) : "winget";

            if (string.IsNullOrWhiteSpace(id)) continue;

            packages.Add(new AppPackage
            {
                Name = name,
                Id = id,
                CurrentVersion = version,
                AvailableVersion = available,
                Source = string.IsNullOrWhiteSpace(source) ? "winget" : source,
                Status = "Pending"
            });
        }
        return packages;
    }

    public async Task<int> UpgradeAsync(string packageId, CancellationToken ct = default)
    {
        var args = $"upgrade --id \"{packageId}\" -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct);
    }

    public async Task<int> UpgradeAllAsync(CancellationToken ct = default)
    {
        var args = "upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown";
        return await _runner.RunProcessAsync("winget", args, ct);
    }
}
