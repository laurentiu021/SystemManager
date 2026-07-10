// SysManager · SystemPaths
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

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
}
