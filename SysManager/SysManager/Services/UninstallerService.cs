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
    /// <summary>
    /// Where Windows keeps uninstall entries, under HKLM for the machine and under HKCU for one user. Declared once for
    /// every reader that lists them: this service, and the Uninstaller's leftover search (#1527).
    /// </summary>
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>The same entries for 32-bit programs on 64-bit Windows, under HKLM only.</summary>
    internal const string UninstallKeyWow64 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IPowerShellRunner _runner;
    private readonly Func<bool> _isElevated;
    private readonly Microsoft.Win32.RegistryKey _machineRoot;
    private readonly Microsoft.Win32.RegistryKey _userRoot;

    public UninstallerService(IPowerShellRunner runner)
        : this(runner, Helpers.AdminHelper.IsElevated)
    {
    }

    /// <summary>The constructor tests use, with every machine-dependent input replaceable.</summary>
    /// <param name="runner">The process seam every uninstaller is launched through.</param>
    /// <param name="isElevated">Replaces the elevation probe, so the standard-integrity rule is testable.</param>
    /// <param name="machineRoot">
    /// Stands in for HKLM when <see cref="IsStillRegistered"/> looks for an uninstall entry; defaults to the real
    /// hive. Tests pass a redirected HKCU subkey, as <see cref="EdgeOneDriveService"/>'s tests do, so the check
    /// runs against a real registry without touching the machine's uninstall list.
    /// </param>
    /// <param name="userRoot">Stands in for HKCU in the same check; defaults to the real hive.</param>
    internal UninstallerService(
        IPowerShellRunner runner, Func<bool> isElevated,
        Microsoft.Win32.RegistryKey? machineRoot = null, Microsoft.Win32.RegistryKey? userRoot = null)
    {
        _runner = runner;
        _isElevated = isElevated;
        _machineRoot = machineRoot ?? Microsoft.Win32.Registry.LocalMachine;
        _userRoot = userRoot ?? Microsoft.Win32.Registry.CurrentUser;
    }

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Runs 'winget list' and parses the table into <see cref="InstalledApp"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query failed, as opposed to finding nothing.</exception>
    public async Task<List<InstalledApp>> ListInstalledAsync(CancellationToken ct = default)
    {
        // The table this run printed, as every winget query here reads it (#2771): one collected from LineReceived
        // would also hold the lines of any other run on this runner.
        var output = await _runner.RunProcessForOutputAsync("winget",
            "list --accept-source-agreements --disable-interactivity", ct).ConfigureAwait(false);

        // A failed query prints no table, and parsing nothing used to read as "Found 0 installed
        // applications" (#2461).
        WingetFailure.ThrowIfQueryFailed(output.ExitCode);
        var apps = ParseListTable([.. output.Lines]);
        EnrichFromRegistry(apps);
        return apps;
    }

    /// <summary>
    /// Uninstall a package by its winget ID. Returns the process exit code.
    /// </summary>
    public async Task<int> UninstallAsync(string packageId, CancellationToken ct = default)
    {
        // Validate packageId before interpolating it into the winget command line. A LOCAL id
        // (MSIX\… or ARP\Machine\X64\…) is accepted here and nowhere else: winget prints those for
        // apps no source matches, and `winget uninstall --id` takes them, but they need a backslash
        // that the general allowlist rightly refuses for every other command line (#2849).
        if (!WingetId.IsValid(packageId) && !WingetId.IsLocalUninstallId(packageId))
            throw new ArgumentException(
                "Windows does not give this app an identifier SysManager can pass to winget safely, "
                + "so it has to be removed from Windows' own Settings > Apps page.", nameof(packageId));

        EnsureStandardIntegrity();

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

        return apps;
    }

    /// <summary>
    /// The uninstall lists Windows keeps, in the order an app is looked up in them: the 64-bit list, the 32-bit
    /// one, then the user's own (per-user installs like Discord, VS Code, etc.).
    /// </summary>
    private (Microsoft.Win32.RegistryKey Root, string Path)[] UninstallLists =>
    [
        (_machineRoot, UninstallKey),
        (_machineRoot, UninstallKeyWow64),
        (_userRoot, UninstallKey),
    ];

    /// <summary>
    /// Reads EstimatedSize and Publisher from the Uninstall registry keys
    /// and enriches the app list. EstimatedSize is in KB.
    /// </summary>
    /// <remarks>
    /// Over the service's own roots, as <see cref="IsStillRegistered"/> reads them. It read the machine's hives
    /// directly, so its tests could only check that it did not throw, and every parse test read the uninstall
    /// list of the machine running it (#2741).
    /// </remarks>
    internal void EnrichFromRegistry(List<InstalledApp> apps)
    {
        if (apps.Count == 0) return;

        var lookup = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            lookup.TryAdd(app.Name, app);
        }

        foreach (var (root, path) in UninstallLists)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;

                EnrichFromRegistryKey(key, lookup);
            }
            catch (System.Security.SecurityException) { /* skip protected registry key */ }
            catch (UnauthorizedAccessException) { /* skip protected registry key */ }
        }
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

                // Both commands from the first entry with an uninstall command, which is the entry Windows lists.
                // They were taken one at a time, so an entry with only a quiet command, a leftover of an older
                // version say, kept its command beside the uninstall command of the next entry of the same name,
                // and the quiet one is the one launched (#2741). An entry with only a quiet command is used until
                // one with an uninstall command turns up.
                var uninst = sub.GetValue("UninstallString") as string;
                var quietUninst = sub.GetValue("QuietUninstallString") as string;
                if (string.IsNullOrWhiteSpace(app.UninstallString) && !string.IsNullOrWhiteSpace(uninst))
                {
                    app.UninstallString = uninst;
                    app.QuietUninstallString = string.IsNullOrWhiteSpace(quietUninst) ? "" : quietUninst;
                }
                else if (string.IsNullOrWhiteSpace(app.UninstallString) && string.IsNullOrWhiteSpace(app.QuietUninstallString)
                    && !string.IsNullOrWhiteSpace(quietUninst))
                {
                    app.QuietUninstallString = quietUninst;
                }

                var installLoc = sub.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(app.InstallLocation) && !string.IsNullOrWhiteSpace(installLoc))
                    app.InstallLocation = installLoc.Trim();

                if (app.Icon is null)
                {
                    var iconPath = sub.GetValue("DisplayIcon") as string;

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
        EnsureStandardIntegrity();

        var command = !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException(
                $"No uninstall command found for '{app.Name}'. The app may need to be removed manually.");

        // Parse the uninstall string into executable + arguments.
        // Handles both quoted paths ("C:\path\uninstall.exe" /S) and unquoted.
        var (exe, args) = ParseUninstallCommand(command);

        // Uninstall execution is restricted to a standard-integrity SysManager process
        // before registry data is parsed. Keep that state explicit for the shared path and
        // payload validators; the selected uninstaller may request its own elevation later
        // through the Windows shell.
        const bool runningElevated = false;

        // Validate the executable exists and is a real file, not a
        // script or arbitrary command (#294). HKCU uninstall keys can be modified
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

            // Resolving the binary to System32 is NOT enough (#906) — rundll32 and
            // MsiExec take their payload from the (HKCU-writable) arguments. rundll32
            // will load ANY DLL at ANY entry point, and MsiExec will run an arbitrary
            // package. Validate the payload before handing the command to the shell.
            args = ValidateTrustedBinaryArgs(resolvedName, args, runningElevated);
            if (resolvedName.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
                args = WithInstallerUi(args);
        }
        else
        {
            // Launch the same canonical path that passed every check. This binds validation to execution and
            // avoids current-directory or dot-segment drift.
            exe = LocalExecutable(exe);
        }

        Log.Information("Uninstalling local app '{Name}' via: {Exe} {Args}", app.Name, exe, args);
        return await _runner.RunProcessWithShellAsync(exe, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True while Windows still lists <paramref name="app"/> as installed: an uninstall entry with its display
    /// name and the same uninstall command it was listed with.
    /// </summary>
    /// <remarks>
    /// An uninstaller's exit code is only what the process SysManager launched returned, and that can be long
    /// before anything is removed (#2448). NSIS uninstallers, for one, copy themselves to the temp folder,
    /// start the copy and exit at once unless given <c>_?=</c>. The launched process returns 0 while the
    /// wizard is still open, or while a silent removal is still running. Requiring the same command, not
    /// just the name, keeps a second installed version of the app from reading as this one.
    /// <para>An unreadable key counts as not registered. The exit code was the only evidence before this
    /// check existed, so when the registry cannot be read the result falls back to it rather than to a new
    /// guess.</para>
    /// </remarks>
    public bool IsStillRegistered(InstalledApp app)
    {
        foreach (var (root, path) in UninstallLists)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null
                        || !string.Equals(sub.GetValue("DisplayName") as string, app.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (SameCommand(sub.GetValue("UninstallString") as string, app.UninstallString)
                        || SameCommand(sub.GetValue("QuietUninstallString") as string, app.QuietUninstallString))
                        return true;
                }
            }
            catch (System.Security.SecurityException ex) { Log.Debug("Uninstall entry check skipped {Path}: {Error}", path, ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Uninstall entry check skipped {Path}: {Error}", path, ex.Message); }
        }
        return false;

        static bool SameCommand(string? registered, string? listed) =>
            !string.IsNullOrWhiteSpace(listed) && string.Equals(registered, listed, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureStandardIntegrity()
    {
        if (_isElevated())
        {
            throw new InvalidOperationException(
                "Uninstall actions are disabled while SysManager is running as administrator. " +
                "Reopen SysManager normally so each uninstaller can request only the privileges it needs.");
        }
    }

    /// <summary>
    /// The full path of <paramref name="exe"/> when it names a program on one of this PC's drives, or throws what
    /// the row shows instead.
    /// </summary>
    /// <param name="exe">The executable an uninstall command names.</param>
    /// <param name="fileExists">
    /// Replaces <see cref="System.IO.File.Exists(string)"/>, so a test can name any folder.
    /// </param>
    /// <remarks>
    /// <para>Where on the drive it is does not matter. An uninstaller runs only while SysManager is not elevated
    /// (<see cref="EnsureStandardIntegrity"/>), so it gets the user's own rights, the same as when it is
    /// double-clicked, or started by Windows Settings from the same uninstall entry. It used to have to be under
    /// Program Files, the Windows folder, ProgramData or Local AppData, which refused an app installed on another
    /// drive or, as some install for one user, under Roaming AppData, and protected nothing: a user can write to
    /// Local AppData as much as to anywhere else (#2783).</para>
    /// <para>Still refused: a path that is not a full one, which ShellExecute would look up from another folder
    /// than the one checked; a network path, since an uninstaller is installed on the PC and looking for one on a
    /// share would reach the machine that entry names; and anything but an .exe, a script among them.</para>
    /// </remarks>
    internal static string LocalExecutable(string exe, Func<string, bool>? fileExists = null)
    {
        if (!System.IO.Path.IsPathFullyQualified(exe))
            throw CannotStart($"its entry does not give the full path to the uninstaller: '{exe}'");

        var fullPath = System.IO.Path.GetFullPath(exe);
        if (!IsOnADrive(fullPath))
            throw CannotStart($"the uninstaller is not on one of this PC's drives: '{exe}'");

        if (!System.IO.Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw CannotStart($"the uninstaller is not a program (.exe): '{exe}'");

        if (!(fileExists ?? System.IO.File.Exists)(fullPath))
            throw new InvalidOperationException(
                $"Uninstall executable not found: '{exe}'. The app may have been removed already.");

        return fullPath;

        // "C:\" and the like. A share ("\\server\share") and a device path ("\\?\C:\") have longer roots.
        static bool IsOnADrive(string path) =>
            System.IO.Path.GetPathRoot(path) is [var letter, ':', '\\'] && char.IsAsciiLetter(letter);
    }

    /// <summary>
    /// The refusal a row shows when SysManager does not start an app's uninstaller: where the app can be removed
    /// instead, then why, for a support request.
    /// </summary>
    /// <remarks>
    /// Each refusal used to end "Refusing to run for security.", which gave the user nothing to do, and which someone
    /// this tab is for reads as a warning about the app (#2783). Windows Settings runs the same uninstall entry, the
    /// way Windows itself handles it.
    /// </remarks>
    internal static InvalidOperationException CannotStart(string why) =>
        new($"Remove it from Windows Settings ▸ Apps. SysManager does not start its uninstaller, because {why}.");

    /// <summary>
    /// Parses an uninstall command string into executable and arguments.
    /// Handles: "C:\path\uninstall.exe" /S, C:\path\uninstall.exe /S,
    /// MsiExec.exe /X{GUID}, rundll32.exe ...
    /// </summary>
    internal static (string Exe, string Args) ParseUninstallCommand(string command)
    {
        command = command.Trim();

        // Case 1: Quoted executable path. Its own characters are not checked: the shell starts the file the quotes
        // name and hands it the rest, and no command interpreter reads the path, so "Smith & Sons" in a folder name
        // chains nothing. It is still bound to a real file before it starts (#2783).
        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var exe = command[1..endQuote];
                var args = endQuote + 1 < command.Length
                    ? command[(endQuote + 1)..].TrimStart()
                    : "";
                RefuseChaining(args, command);
                return (exe, args);
            }
        }

        // Reject obviously malicious patterns before parsing the other forms (#403): with no quotes, nothing tells
        // the executable from its arguments until the parse below has guessed.
        RefuseChaining(command, command);

        // Case 2: MsiExec — common pattern: MsiExec.exe /I{GUID} or /X{GUID}
        if (command.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            var spaceIdx = command.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var exe = command[..spaceIdx];
                var args = command[(spaceIdx + 1)..].TrimStart();
                // Convert /I (modify) to /X (uninstall) if needed. The switches it runs with are set at launch, by
                // WithInstallerUi, for this form and a quoted MsiExec path alike.
                // Use regex to match /I only as a standalone switch (not inside GUIDs).
                args = MsiInstallSwitchPattern().Replace(args, "/X");
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
        // Since #403, only match ".exe" followed by end-of-string, whitespace, or a
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

        // No fallback — if we can't parse it safely, reject it (#403).
        // The old fallback treated the entire string as an executable, which
        // could execute arbitrary commands if the string was crafted.
        throw CannotStart($"its uninstall command names no program: '{command}'");
    }

    /// <summary>
    /// Refuses <paramref name="text"/> when it holds a shell metacharacter that could chain a second command, should
    /// a command interpreter be what reads it (#403).
    /// </summary>
    private static void RefuseChaining(string text, string command)
    {
        if (text.Contains('|') || text.Contains('&') ||
            text.Contains(';') || text.Contains('`') ||
            text.Contains("$("))
            throw CannotStart($"its uninstall command has characters that could start a second command: '{command}'");
    }

    /// <summary>
    /// Checks whether the given absolute path resides under an approved directory.
    /// Windows installation roots are accepted in either integrity mode. Shared and
    /// per-user application-data roots are accepted only when <paramref name="isElevated"/>
    /// is false because their contents can be created by an unprivileged user.
    /// </summary>
    internal static bool IsUnderTrustedDirectory(string fullPath, bool isElevated)
    {
        var trustedDirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        // Shared and per-user app-data locations can contain user-created payloads.
        // Accept them only at standard integrity, where executing such a payload cannot
        // inherit an administrator token from SysManager.
        if (!isElevated)
        {
            trustedDirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            trustedDirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

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
    /// Validates the arguments passed to a trusted system binary (rundll32 /
    /// MsiExec) before launch, because those arguments come from the HKCU-writable
    /// registry UninstallString and can select an arbitrary executable payload (#906).
    /// </summary>
    /// <remarks>
    /// rundll32: the leading token (before the first comma) is the DLL path; it must
    /// resolve under a trusted directory or we refuse to run. MsiExec: the arguments
    /// must be a product-code uninstall (/X{GUID}); anything else (e.g. an arbitrary
    /// package path or /I install) is rejected.
    /// </remarks>
    internal static string ValidateTrustedBinaryArgs(
        string resolvedExeName,
        string args,
        bool isElevated)
    {
        if (resolvedExeName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            // rundll32 syntax: <dll>[,<entrypoint> [<args>]]. Bind validation and
            // execution to one canonical DLL path before passing it to the system binary.
            var commaIndex = args.IndexOf(',');
            var dllToken = commaIndex >= 0 ? args[..commaIndex] : args;
            var dll = dllToken.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dll))
                throw CannotStart("its rundll32 command names no DLL");

            string dllFullPath;
            if (System.IO.Path.IsPathFullyQualified(dll))
            {
                dllFullPath = System.IO.Path.GetFullPath(dll);
            }
            else if (!System.IO.Path.IsPathRooted(dll) &&
                     !dll.Contains('\\') &&
                     !dll.Contains('/'))
            {
                // Bare DLL names are a common Windows uninstall form. Resolve them only
                // against System32, never PATH or SysManager's current directory.
                dllFullPath = System.IO.Path.Combine(Environment.SystemDirectory, dll);
            }
            else
            {
                throw CannotStart($"its rundll32 command does not give the full path to its DLL: '{dll}'");
            }

            if (!System.IO.File.Exists(dllFullPath))
                throw CannotStart($"the DLL its rundll32 command loads is not on this PC: '{dllFullPath}'");

            // Kept to the folders apps install to, though the executable no longer is (#2783): rundll32 loads
            // whatever file it is named, and each of the five rundll32 uninstall commands on the PC this was
            // measured on loads its DLL from one of them. A user-writable folder among them is trusted only when not
            // elevated (#906), so rundll32 cannot load an attacker-planted DLL with SysManager's elevation.
            if (!IsUnderTrustedDirectory(dllFullPath, isElevated))
                throw CannotStart($"its rundll32 command loads a DLL outside the folders apps install to: '{dll}'");

            var suffix = commaIndex >= 0 ? args[commaIndex..] : string.Empty;
            return $"\"{dllFullPath}\"{suffix}";
        }

        if (resolvedExeName.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Only allow a product-code uninstall (/X{GUID}); reject arbitrary packages.
            if (!MsiUninstallArgsPattern().IsMatch(args))
                throw CannotStart($"its Windows Installer command is not a plain removal: '{args}'");
        }

        return args;
    }

    /// <summary>
    /// The arguments an MSI uninstall runs with: a progress bar rather than a silent session, so Windows Installer can
    /// ask for administrator rights, and no restart unless the entry names how to handle one.
    /// </summary>
    /// <remarks>
    /// SysManager uninstalls only at standard integrity, and removing a package installed for all users needs
    /// administrator rights. Windows Installer asks for them only when it has a user interface: under <c>/quiet</c> or
    /// <c>/qn</c> it cannot, and the removal failed with 1603 instead (#2724). Those become <c>/passive</c>, a
    /// progress bar only, which is also added when no interface switch is given; <c>/qb</c>, <c>/qr</c> and
    /// <c>/qf</c> show one and are kept. Called on arguments <see cref="ValidateTrustedBinaryArgs"/> accepted, so
    /// every token is the product code or a switch from its list.
    /// </remarks>
    internal static string WithInstallerUi(string args)
    {
        var tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var silent = tokens.FindIndex(IsSilent);
        tokens.RemoveAll(IsSilent);
        if (!tokens.Exists(IsInterfaceLevel)) tokens.Insert(silent >= 0 ? silent : tokens.Count, "/passive");
        if (!tokens.Exists(IsRestartOption)) tokens.Add("/norestart");
        return string.Join(' ', tokens);

        static bool IsSilent(string token) => token.ToUpperInvariant() is "/QUIET" or "/QN";
        static bool IsInterfaceLevel(string token) =>
            token.ToUpperInvariant() is "/PASSIVE" or "/QB" or "/QB+" or "/QB!" or "/QR" or "/QF";
        static bool IsRestartOption(string token) =>
            token.ToUpperInvariant() is "/NORESTART" or "/PROMPTRESTART" or "/FORCERESTART";
    }

    // Matches a product-code uninstall such as "/X{0F2C3A4B-...}" optionally followed
    // by /quiet, /qn, /norestart and similar switches. Requires the /X{GUID} form so a
    // crafted UninstallString cannot make MsiExec run an arbitrary package path.
    [GeneratedRegex(
        @"^\s*/x\s*\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}(?:\s+/(?:quiet|passive|norestart|promptrestart|forcerestart|q(?:n|b[+!]?|r|f)))*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiUninstallArgsPattern();
}
