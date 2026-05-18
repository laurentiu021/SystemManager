// SysManager · UninstallerService — list and uninstall apps via winget and registry
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
        // Validate packageId: allowlist alphanumeric, dots, hyphens, underscores,
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
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w.\-/+ ]{1,256}$")]
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
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;

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

                if (string.IsNullOrWhiteSpace(app.UninstallString))
                {
                    var quietUninst = sub.GetValue("QuietUninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(quietUninst))
                        app.QuietUninstallString = quietUninst;

                    var uninst = sub.GetValue("UninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(uninst))
                        app.UninstallString = uninst;
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

    /// <summary>
    /// Uninstalls a local application using its registry UninstallString.
    /// Prefers QuietUninstallString when available.
    /// </summary>
    public async Task<int> UninstallLocalAsync(InstalledApp app, CancellationToken ct = default)
    {
        var command = !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException(
                $"No uninstall command found for '{app.Name}'. The app may need to be removed manually.");

        // Parse the uninstall string into executable + arguments.
        // Handles both quoted paths ("C:\path\uninstall.exe" /S) and unquoted.
        var (exe, args) = ParseUninstallCommand(command);

        // SEC-002: Validate the executable exists and is a real file (not a
        // script or arbitrary command). HKCU uninstall keys can be modified
        // without admin, so we must not blindly execute whatever is there.
        // Use exact filename match for system binaries to prevent bypass via
        // similarly-named executables (e.g. "MsiExecEvil.exe").
        // Resolve trusted binaries to absolute System32 path to prevent PATH hijacking.
        var exeFileName = System.IO.Path.GetFileName(exe);
        var isTrustedSystemBinary =
            exeFileName.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase) ||
            exeFileName.Equals("MsiExec", StringComparison.OrdinalIgnoreCase) ||
            exeFileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
            exeFileName.Equals("rundll32", StringComparison.OrdinalIgnoreCase);

        if (isTrustedSystemBinary)
        {
            // Resolve to absolute path in System32 to prevent PATH hijacking
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var resolvedName = exeFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? exeFileName : exeFileName + ".exe";
            exe = System.IO.Path.Join(systemDir, resolvedName);
        }
        else
        {
            if (!System.IO.File.Exists(exe))
                throw new InvalidOperationException(
                    $"Uninstall executable not found: '{exe}'. The app may have been removed already.");

            var ext = System.IO.Path.GetExtension(exe);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Uninstall target is not an executable (.exe): '{exe}'. Refusing to run for security.");

            // SEC-H2: Validate the executable resides under a trusted directory.
            // Registry uninstall keys (especially HKCU) can be modified without admin,
            // so we must not execute arbitrary paths. Allow: Program Files, Windows,
            // ProgramData, and LocalApplicationData (per-user installs like VS Code).
            var fullPath = System.IO.Path.GetFullPath(exe);
            if (!IsUnderTrustedDirectory(fullPath))
                throw new InvalidOperationException(
                    $"Uninstall executable is outside trusted directories: '{exe}'. Refusing to run for security.");
        }

        Log.Information("Uninstalling local app '{Name}' via: {Exe} {Args}", app.Name, exe, args);
        return await _runner.RunProcessAsync(exe, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an uninstall command string into executable and arguments.
    /// Handles: "C:\path\uninstall.exe" /S, C:\path\uninstall.exe /S,
    /// MsiExec.exe /X{GUID}, rundll32.exe ...
    /// </summary>
    internal static (string Exe, string Args) ParseUninstallCommand(string command)
    {
        command = command.Trim();

        // SEC-M7: Reject obviously malicious patterns before parsing.
        // Commands containing shell metacharacters that could chain commands.
        if (command.Contains('|') || command.Contains('&') ||
            command.Contains(';') || command.Contains('`') ||
            command.Contains("$("))
            throw new InvalidOperationException(
                $"Uninstall command contains shell metacharacters — refusing to parse: '{command}'");

        // Case 1: Quoted executable path
        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var exe = command[1..endQuote];
                var args = endQuote + 1 < command.Length
                    ? command[(endQuote + 1)..].TrimStart()
                    : "";
                return (exe, args);
            }
        }

        // Case 2: MsiExec — common pattern: MsiExec.exe /I{GUID} or /X{GUID}
        if (command.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = command.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var exe = command[..spaceIdx];
                var args = command[(spaceIdx + 1)..].TrimStart();
                // Convert /I (modify) to /X (uninstall) if needed, add /quiet.
                // Use regex to match /I only as a standalone switch (not inside GUIDs).
                args = System.Text.RegularExpressions.Regex.Replace(
                    args, @"(?<![A-Za-z0-9])/I(?![A-Za-z0-9])", "/X",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!args.Contains("/quiet", StringComparison.OrdinalIgnoreCase)
                    && !args.Contains("/qn", StringComparison.OrdinalIgnoreCase))
                    args += " /quiet /norestart";
                return (exe, args);
            }
        }

        // Case 3: rundll32 — pass as-is
        if (command.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = command.IndexOf(' ');
            if (spaceIdx > 0)
                return (command[..spaceIdx], command[(spaceIdx + 1)..].TrimStart());
        }

        // Case 4: Unquoted path with spaces — find first .exe boundary.
        // SEC-M7: Only match ".exe" followed by end-of-string, whitespace, or a
        // switch character (/,-). This prevents misparsing paths like
        // "C:\dir\app.executable\tool.exe" where an earlier ".exe" substring
        // would incorrectly split the path.
        var searchStart = 0;
        while (searchStart < command.Length)
        {
            var exeEnd = command.IndexOf(".exe", searchStart, StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0) break;

            exeEnd += 4; // include ".exe"
            // Valid boundary: end of string, or followed by whitespace/switch
            if (exeEnd >= command.Length ||
                command[exeEnd] == ' ' || command[exeEnd] == '\t' ||
                command[exeEnd] == '/' || command[exeEnd] == '-')
            {
                var exe = command[..exeEnd].Trim();
                var args = exeEnd < command.Length ? command[exeEnd..].TrimStart() : "";
                return (exe, args);
            }
            // Not a valid boundary — keep searching after this occurrence
            searchStart = exeEnd;
        }

        // SEC-M7: No fallback — if we can't parse it safely, reject it.
        // The old fallback treated the entire string as an executable, which
        // could execute arbitrary commands if the string was crafted.
        throw new InvalidOperationException(
            $"Cannot safely parse uninstall command — no valid executable found: '{command}'");
    }

    /// <summary>
    /// Checks whether the given absolute path resides under a trusted system directory
    /// (Program Files, Windows, ProgramData, or LocalApplicationData).
    /// </summary>
    private static bool IsUnderTrustedDirectory(string fullPath)
    {
        var trustedDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        return trustedDirs.Any(dir =>
            !string.IsNullOrEmpty(dir) &&
            fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
    }
}
