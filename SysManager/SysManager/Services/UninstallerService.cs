// SysManager · UninstallerService — list and uninstall apps via winget and registry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to list installed packages and uninstall them.
/// </summary>
public sealed partial class UninstallerService
{
    private readonly IPowerShellRunner _runner;

    public UninstallerService(IPowerShellRunner runner) => _runner = runner;

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
                "list --accept-source-agreements --disable-interactivity", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        return ParseListTable(captured.ToList());
    }

    /// <summary>
    /// Uninstall a package by its winget ID. Returns the process exit code.
    /// </summary>
    public async Task<int> UninstallAsync(string packageId, CancellationToken ct = default)
    {
        // Validate packageId before interpolating it into the winget command line.
        if (!WingetId.IsValid(packageId))
            throw new ArgumentException("Invalid package ID.", nameof(packageId));

        var args = $"uninstall --id \"{packageId}\" -e --silent --accept-source-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^\s*Name\s+Id\s+Version", RegexOptions.IgnoreCase)]
    private static partial Regex ListHeaderPattern();

    [GeneratedRegex(@"^\d+\s+packages?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex PackageSummaryPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/I(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitchPattern();

    internal static List<InstalledApp> ParseListTable(List<string> lines)
    {
        var rows = Helpers.WingetTableParser.Parse(lines, ListHeaderPattern(), PackageSummaryPattern());
        var apps = new List<InstalledApp>(rows.Count);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length < 2) continue;

            apps.Add(new InstalledApp
            {
                Name = row.Name,
                Id = row.Id,
                Version = row.Version,
                Source = string.IsNullOrWhiteSpace(row.Source) ? "" : row.Source,
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
                if (key is null) continue;

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
            if (hkcuKey is not null)
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
                if (sub is null) continue;

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

                if (app.Icon is null)
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

        // SEC-LPE: capture our own elevation up front. The UninstallString (and any
        // rundll32 DLL path it carries) comes from a registry key that a standard user
        // can write — HKCU especially. When SysManager itself runs elevated, executing
        // a binary from a user-writable location would let an unprivileged attacker who
        // planted it gain our elevation (local privilege escalation). The trusted-path
        // checks below therefore tighten when elevated.
        var isElevated = Helpers.AdminHelper.IsElevated();

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

            // SEC-LPE: resolving the binary to System32 is NOT enough — rundll32 and
            // MsiExec take their payload from the (HKCU-writable) arguments. rundll32
            // will load ANY DLL at ANY entry point, and MsiExec will run an arbitrary
            // package; both inherit our elevation. Validate the payload before launch.
            ValidateTrustedBinaryArgs(resolvedName, args, isElevated);
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

            // SEC-H2 / SEC-LPE: Validate the executable resides under a trusted directory.
            // Registry uninstall keys (especially HKCU) can be modified without admin,
            // so we must not execute arbitrary paths. Admin-protected dirs (Program Files,
            // Windows, ProgramData) are always trusted; the user-writable per-user location
            // (LocalApplicationData) is trusted ONLY when we are not elevated — otherwise an
            // unprivileged attacker who dropped a binary there would gain our elevation.
            var fullPath = System.IO.Path.GetFullPath(exe);
            if (!IsUnderTrustedDirectory(fullPath, isElevated))
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
                args = MsiInstallSwitchPattern().Replace(args, "/X");
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
    /// Checks whether the given absolute path resides under a trusted directory.
    /// Admin-protected directories (Program Files, Windows, ProgramData) are always
    /// trusted. The user-writable per-user location (LocalApplicationData) is trusted
    /// ONLY when <paramref name="isElevated"/> is false — when we run elevated, a binary
    /// there could have been planted by an unprivileged attacker, so trusting it would
    /// be a local privilege-escalation vector (SEC-LPE).
    /// </summary>
    internal static bool IsUnderTrustedDirectory(string fullPath, bool isElevated)
    {
        var trustedDirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData"
        };

        // Per-user install locations (e.g. VS Code, Discord) are writable without admin.
        // Trust them only when we are NOT elevated, so an elevated uninstall can never
        // execute an attacker-planted binary from a user-writable directory.
        if (!isElevated)
            trustedDirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        // Compare on a directory boundary, not a raw prefix. A bare StartsWith lets
        // "C:\Program Files Evil\x.exe" pass the "C:\Program Files" check — so append
        // a trailing separator to both sides before comparing.
        static string WithSep(string p) =>
            p.EndsWith(System.IO.Path.DirectorySeparatorChar) ? p : p + System.IO.Path.DirectorySeparatorChar;

        var candidate = WithSep(System.IO.Path.GetFullPath(fullPath));
        return trustedDirs.Any(dir =>
            !string.IsNullOrEmpty(dir) &&
            candidate.StartsWith(WithSep(System.IO.Path.GetFullPath(dir)), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SEC-LPE: validates the arguments passed to a trusted system binary (rundll32 /
    /// MsiExec) before launch, because those arguments come from the HKCU-writable
    /// registry UninstallString and the binary executes them with our elevation.
    /// </summary>
    /// <remarks>
    /// rundll32: the leading token (before the first comma) is the DLL path; it must
    /// resolve under a trusted directory or we refuse to run. MsiExec: the arguments
    /// must be a product-code uninstall (/X{GUID}); anything else (e.g. an arbitrary
    /// package path or /I install) is rejected.
    /// </remarks>
    internal static void ValidateTrustedBinaryArgs(string resolvedExeName, string args, bool isElevated)
    {
        if (resolvedExeName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            // rundll32 syntax: <dll>[,<entrypoint> [<args>]]. Extract the DLL path.
            var dll = args.Split(',', 2)[0].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dll))
                throw new InvalidOperationException(
                    "rundll32 uninstall command has no DLL path — refusing to run for security.");

            // Same SEC-LPE rule as the executable check: a user-writable DLL path is only
            // trusted when not elevated, so rundll32 can't load attacker-planted DLLs with
            // our elevation.
            var dllFullPath = System.IO.Path.GetFullPath(dll);
            if (!IsUnderTrustedDirectory(dllFullPath, isElevated))
                throw new InvalidOperationException(
                    $"rundll32 would load a DLL outside trusted directories: '{dll}'. Refusing to run for security.");
        }
        else if (resolvedExeName.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Only allow a product-code uninstall (/X{GUID}); reject arbitrary packages.
            if (!MsiUninstallArgsPattern().IsMatch(args))
                throw new InvalidOperationException(
                    $"MsiExec uninstall arguments are not a recognized product-code uninstall: '{args}'. " +
                    "Refusing to run for security.");
        }
    }

    // Matches a product-code uninstall such as "/X{0F2C3A4B-...}" optionally followed
    // by /quiet, /qn, /norestart and similar switches. Requires the /X{GUID} form so a
    // crafted UninstallString cannot make MsiExec run an arbitrary package path.
    [GeneratedRegex(@"^\s*/x\s*\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.IgnoreCase)]
    private static partial Regex MsiUninstallArgsPattern();
}
