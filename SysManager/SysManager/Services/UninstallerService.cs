// SysManager · UninstallerService — list and uninstall apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to list installed packages and uninstall them.
/// </summary>
public sealed partial class UninstallerService
{
    private readonly PowerShellRunner _runner;

    public UninstallerService(PowerShellRunner runner) => _runner = runner;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Runs 'winget list' and parses the table into <see cref="InstalledApp"/>.
    /// </summary>
    public async Task<List<InstalledApp>> ListInstalledAsync(CancellationToken ct = default)
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
                "list --accept-source-agreements --disable-interactivity", ct);
        }
        finally { _runner.LineReceived -= Collect; }

        return ParseListTable(captured);
    }

    /// <summary>
    /// Uninstall a package by its winget ID. Returns the process exit code.
    /// </summary>
    public async Task<int> UninstallAsync(string packageId, CancellationToken ct = default)
    {
        // Validate packageId: whitelist alphanumeric, dots, hyphens, underscores,
        // forward slashes (scoped IDs like "Microsoft.VisualStudio.2022.Community"),
        // and plus signs (e.g. "Notepad++.Notepad++").
        if (string.IsNullOrWhiteSpace(packageId)
            || !PackageIdPattern().IsMatch(packageId))
            throw new ArgumentException("Invalid package ID.", nameof(packageId));

        var args = $"uninstall --id \"{packageId}\" -e --silent --accept-source-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches valid winget package IDs: alphanumeric, dots, hyphens,
    /// underscores, forward slashes, plus signs, and spaces. Max 256 chars.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w.\-/+\s]{1,256}$")]
    private static partial System.Text.RegularExpressions.Regex PackageIdPattern();

    internal static List<InstalledApp> ParseListTable(List<string> lines)
    {
        var apps = new List<InstalledApp>();

        // Find header line: "Name   Id   Version  [Available]  Source"
        int headerIdx = lines.FindIndex(l =>
            Regex.IsMatch(l, @"^\s*Name\s+Id\s+Version", RegexOptions.IgnoreCase));
        if (headerIdx < 0) return apps;

        var header = lines[headerIdx];
        int idxId = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        int idxVersion = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        int idxAvailable = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
        int idxSource = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);
        if (idxId < 0 || idxVersion < 0) return apps;

        // Version end boundary: Available if present, else Source, else line end
        int versionEnd = idxAvailable > 0 ? idxAvailable
                       : idxSource > 0 ? idxSource
                       : -1;

        for (int i = headerIdx + 2; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("--")) continue;
            // Stop at summary lines like "123 packages installed"
            if (Regex.IsMatch(line, @"^\d+\s+packages?\s+", RegexOptions.IgnoreCase)) break;
            if (line.Length < idxVersion) continue;

            string Slice(int start, int end) =>
                start < line.Length
                    ? line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim()
                    : string.Empty;

            var name = Slice(0, idxId);
            var id = Slice(idxId, idxVersion);
            var version = Slice(idxVersion, versionEnd);
            var source = idxSource > 0 ? Slice(idxSource, -1) : "";

            if (string.IsNullOrWhiteSpace(id)) continue;
            if (string.IsNullOrWhiteSpace(name)) continue;

            apps.Add(new InstalledApp
            {
                Name = name,
                Id = id,
                Version = version,
                Source = string.IsNullOrWhiteSpace(source) ? "" : source,
                Status = ""
            });
        }

        EnrichFromRegistry(apps);
        return apps;
    }

    /// <summary>
    /// Reads EstimatedSize and Publisher from the Uninstall registry keys
    /// and enriches the app list. EstimatedSize is in KB.
    /// </summary>
    internal static void EnrichFromRegistry(List<InstalledApp> apps)
    {
        if (apps.Count == 0) return;

        var lookup = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            lookup.TryAdd(app.Name, app);
        }

        var regPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var regPath in regPaths)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                if (key == null) continue;

                EnrichFromRegistryKey(key, lookup);
            }
            catch (System.Security.SecurityException) { /* skip protected registry key */ }
            catch (UnauthorizedAccessException) { /* skip protected registry key */ }
        }

        // Also scan HKCU (per-user installs like Discord, VS Code, etc.)
        try
        {
            using var hkcuKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (hkcuKey != null)
                EnrichFromRegistryKey(hkcuKey, lookup);
        }
        catch (System.Security.SecurityException) { /* skip protected HKCU key */ }
        catch (UnauthorizedAccessException) { /* skip protected HKCU key */ }
    }

    private static void EnrichFromRegistryKey(
        Microsoft.Win32.RegistryKey key,
        Dictionary<string, InstalledApp> lookup)
    {
        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub == null) continue;

                var displayName = sub.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                if (!lookup.TryGetValue(displayName, out var app)) continue;

                if (app.SizeBytes == 0)
                {
                    var sizeKb = sub.GetValue("EstimatedSize");
                    if (sizeKb is int kb && kb > 0)
                        app.SizeBytes = kb * 1024L;
                }

                if (string.IsNullOrWhiteSpace(app.Publisher))
                {
                    var pub = sub.GetValue("Publisher") as string;
                    if (!string.IsNullOrWhiteSpace(pub))
                        app.Publisher = pub;
                }

                if (app.Icon == null)
                {
                    var iconPath = sub.GetValue("DisplayIcon") as string;
                    var installLoc = sub.GetValue("InstallLocation") as string;

                    if (!string.IsNullOrWhiteSpace(iconPath))
                    {
                        var commaIdx = iconPath.LastIndexOf(',');
                        if (commaIdx > 0)
                            iconPath = iconPath[..commaIdx].Trim('"', ' ');
                    }

                    app.Icon = IconExtractorService.GetInstalledAppIcon(
                        iconPath, installLoc, app.Name);
                }
            }
            catch (System.Security.SecurityException) { /* skip protected subkey */ }
            catch (UnauthorizedAccessException) { /* skip protected subkey */ }
        }
    }
}
