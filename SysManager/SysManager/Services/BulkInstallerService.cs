// SysManager · BulkInstallerService — install apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to install packages by their winget ID.
/// </summary>
public sealed partial class BulkInstallerService
{
    private readonly IPowerShellRunner _runner;

    public BulkInstallerService(IPowerShellRunner runner) => _runner = runner;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Installs a package by its winget ID. Returns the process exit code.
    /// </summary>
    public async Task<int> InstallAsync(string wingetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wingetId)
            || !PackageIdPattern().IsMatch(wingetId))
            throw new ArgumentException("Invalid package ID.", nameof(wingetId));

        var args = $"install --id \"{wingetId}\" -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches valid winget package IDs: alphanumeric, dots, hyphens,
    /// underscores, forward slashes, plus signs, and spaces. Max 256 chars.
    /// </summary>
    [GeneratedRegex(@"^[\w.\-/+ ]{1,256}$")]
    private static partial Regex PackageIdPattern();
}
