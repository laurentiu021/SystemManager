// SysManager · SystemPaths
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Linq;

namespace SysManager.Helpers;

/// <summary>
/// Resolves bare Windows tool names (sfc.exe, netsh.exe, reg.exe, schtasks.exe, powercfg.exe,
/// powershell.exe, …) to their full trusted paths under %SystemRoot%\System32.
/// <para>
/// Launching a system tool by bare filename with <c>UseShellExecute=false</c> lets the Win32
/// <c>CreateProcess</c> search order look in the CALLING process's own directory FIRST. SysManager
/// ships as a single portable .exe that users often run from a user-writable location (Downloads),
/// sometimes elevated — so an attacker-planted <c>netsh.exe</c> / <c>reg.exe</c> next to it would be
/// executed with administrator rights (binary-planting / local privilege escalation). Pinning the
/// full System32 path closes that vector while leaving behaviour otherwise identical.
/// </para>
/// </summary>
internal static class SystemPaths
{
    private static readonly string System32 = Environment.SystemDirectory;

    /// <summary>
    /// Returns the full trusted path for a bare Windows tool name that exists under System32
    /// (or the boxed Windows PowerShell 5.1 folder). Names that are already rooted / contain a
    /// path separator, or that are not found in a trusted location, are returned unchanged so
    /// callers of non-system executables and explicit paths are never altered.
    /// </summary>
    public static string ResolveSystemTool(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        if (Path.IsPathRooted(fileName) || fileName.Contains('\\') || fileName.Contains('/'))
            return fileName;

        // winget is NOT a System32 tool — it's an MSIX app whose per-user execution alias lives
        // in the user-WRITABLE %LOCALAPPDATA%\Microsoft\WindowsApps. Resolving it here to its
        // trusted, admin-only-writable install path (and failing CLOSED, never to the bare name)
        // is handled separately.
        if (fileName.Equals("winget", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
            return ResolveWinget();

        // Try the name as given, then with a ".exe" suffix. Callers that pass a bare name
        // WITHOUT the extension (e.g. "powershell") would otherwise never match on disk —
        // File.Exists("...\\powershell") is false, only "...\\powershell.exe" exists — so the
        // method would fall through and return the unrooted bare name, re-opening the exact
        // binary-planting/LPE vector this helper exists to close. Probing "<name>.exe" as a
        // fallback makes the guard robust to that mistake (defense-in-depth).
        var candidates = fileName.Contains('.')
            ? new[] { fileName }
            : new[] { fileName, fileName + ".exe" };

        foreach (var name in candidates)
        {
            var direct = Path.Combine(System32, name);
            if (File.Exists(direct)) return direct;

            // Windows PowerShell 5.1 lives in a System32 subfolder, not System32 itself.
            var powerShell = Path.Combine(System32, "WindowsPowerShell", "v1.0", name);
            if (File.Exists(powerShell)) return powerShell;
        }

        return fileName;
    }

    // The fixed publisher-hash suffix of Microsoft's App Installer (winget) MSIX package. Part of
    // the trust anchor: only a folder ending in this — under %ProgramFiles%\WindowsApps, which is
    // writable by administrators/TrustedInstaller only — is accepted as the real winget.
    private const string AppInstallerPackageSuffix = "__8wekyb3d8bbwe";

    /// <summary>
    /// Resolves <c>winget</c> to the App Installer's real, admin-only-writable binary under
    /// <c>%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe\winget.exe</c>,
    /// picking the highest package version present.
    /// <para>
    /// Why this exists: winget is an MSIX execution alias, NOT a System32 tool, so
    /// <see cref="ResolveSystemTool"/>'s System32 probes never match and would return the bare name
    /// <c>"winget"</c>. Launched with <c>UseShellExecute=false</c>, an unrooted name lets Win32
    /// <c>CreateProcess</c> search the calling process's OWN directory FIRST — so an attacker-planted
    /// <c>winget.exe</c> beside SysManager's portable .exe (often run from a user-writable folder,
    /// sometimes elevated) would run with the app's privileges. The per-user alias in the
    /// user-writable <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> is itself untrusted for an elevated
    /// launch, so it is deliberately NOT used.
    /// </para>
    /// <para>
    /// Fails CLOSED: if no trusted install is found it returns a ROOTED, non-plantable path
    /// (<c>System32\winget.exe</c>, which normally does not exist) rather than the bare name — so a
    /// missing App Installer surfaces the same <c>Win32Exception</c> the winget callers already
    /// handle, and can never resolve to the app directory.
    /// </para>
    /// </summary>
    public static string ResolveWinget()
    {
        var rootedFallback = Path.Combine(System32, "winget.exe");
        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            if (!Directory.Exists(windowsApps)) return rootedFallback;

            // Only Microsoft's App Installer package folders (fixed publisher hash). Folder name is
            // Microsoft.DesktopAppInstaller_<version>_<arch>__<hash>; order by the parsed <version>
            // (numeric, so 1.29 > 1.9) descending, and take the first that actually contains
            // winget.exe (the _neutral_~_ resources package, e.g., does not).
            var candidate = Directory
                .EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*" + AppInstallerPackageSuffix)
                .OrderByDescending(ParsePackageVersion)
                .Select(d => Path.Combine(d, "winget.exe"))
                .FirstOrDefault(File.Exists);

            return candidate ?? rootedFallback;
        }
        catch (IOException) { return rootedFallback; }
        catch (UnauthorizedAccessException) { return rootedFallback; }
    }

    /// <summary>
    /// Parses the <c>&lt;version&gt;</c> segment out of a
    /// <c>Microsoft.DesktopAppInstaller_&lt;version&gt;_&lt;arch&gt;__&lt;hash&gt;</c> folder path so
    /// versions sort numerically (1.29.279.0 &gt; 1.9.x). Returns <see cref="System.Version"/> 0.0 for
    /// any unexpected shape so it sorts last rather than throwing.
    /// </summary>
    internal static Version ParsePackageVersion(string packageDir)
    {
        var name = Path.GetFileName(packageDir);
        var parts = name.Split('_');
        // parts[0] = "Microsoft.DesktopAppInstaller", parts[1] = version.
        if (parts.Length >= 2 && Version.TryParse(parts[1], out var version))
            return version;
        return new Version(0, 0);
    }
}
