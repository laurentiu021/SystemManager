// SysManager · UninstallerService — list and uninstall apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to list installed packages and uninstall them.
/// </summary>
public sealed class UninstallerService
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
        var args = $"uninstall --id \"{packageId}\" -e --silent --accept-source-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct);
    }

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
        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Name) && !lookup.ContainsKey(app.Name))
                lookup[app.Name] = app;
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
                            if (!string.IsNullOrWhiteSpace(iconPath))
                            {
                                // DisplayIcon can be "path.exe,0" — strip the index
                                var commaIdx = iconPath.LastIndexOf(',');
                                if (commaIdx > 0)
                                    iconPath = iconPath[..commaIdx].Trim('"', ' ');
                                app.Icon = IconExtractorService.GetIcon(iconPath);
                            }
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
