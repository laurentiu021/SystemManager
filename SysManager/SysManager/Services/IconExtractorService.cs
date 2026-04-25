// SysManager · IconExtractorService — extract exe/file icons for display
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SysManager.Services;

/// <summary>
/// Extracts application icons from executable files using the Windows Shell API.
/// Uses multiple strategies to resolve paths and provides contextual fallback
/// icons (Windows logo for system processes, gear for services, generic app icon).
/// Results are cached per-path to avoid repeated extraction.
/// </summary>
public sealed class IconExtractorService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Lazy-initialized contextual fallback icons (shell32.dll icon indices)
    private static readonly Lazy<ImageSource?> _appFallback = new(() => ExtractShell32Icon(2));
    private static readonly Lazy<ImageSource?> _windowsIcon = new(() => ExtractShell32Icon(44));
    private static readonly Lazy<ImageSource?> _gearIcon    = new(() => ExtractShell32Icon(15));

    /// <summary>
    /// Gets the icon for the given file path or command string.
    /// Tries multiple strategies to resolve the path before falling back.
    /// </summary>
    public static ImageSource? GetIcon(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return _appFallback.Value;

        var normalized = NormalizePath(filePath);
        if (string.IsNullOrEmpty(normalized))
            return _appFallback.Value;

        return _cache.GetOrAdd(normalized, static path => ExtractIcon(path));
    }

    /// <summary>
    /// Gets the icon for a running process. Uses FilePath if available,
    /// otherwise searches common system directories by process name.
    /// Returns a Windows icon for known system processes.
    /// </summary>
    public static ImageSource? GetProcessIcon(string? filePath, string? processName)
    {
        // If we have a valid file path, use it directly
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var icon = GetIcon(filePath);
            if (icon != _appFallback.Value && icon != null)
                return icon;
        }

        // Try to find the exe by process name in common locations
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var found = FindExecutableByName(processName);
            if (found != null)
                return GetIcon(found);

            // Known Windows system processes → Windows icon
            if (IsWindowsSystemProcess(processName))
                return _windowsIcon.Value;

            // Known service host processes → gear icon
            if (IsServiceProcess(processName))
                return _gearIcon.Value;
        }

        // If we had a filePath but it didn't exist, check if it's a Windows path
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (filePath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
                return _windowsIcon.Value;
        }

        return _appFallback.Value;
    }

    /// <summary>
    /// Gets the icon for an installed app. Tries DisplayIcon path first,
    /// then InstallLocation, then falls back to generic app icon.
    /// </summary>
    public static ImageSource? GetInstalledAppIcon(string? displayIconPath, string? installLocation, string? appName)
    {
        // Try DisplayIcon first
        if (!string.IsNullOrWhiteSpace(displayIconPath))
        {
            var icon = GetIcon(displayIconPath);
            if (icon != _appFallback.Value && icon != null)
                return icon;
        }

        // Try to find an exe in InstallLocation
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var exes = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    var match = exes.FirstOrDefault(e =>
                        Path.GetFileNameWithoutExtension(e)
                            .Contains(appName.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return GetIcon(match);
                }
                if (exes.Length > 0)
                    return GetIcon(exes[0]);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return _appFallback.Value;
    }

    /// <summary>Contextual fallback: generic application icon.</summary>
    public static ImageSource? FallbackIcon => _appFallback.Value;

    /// <summary>Contextual fallback: Windows system icon.</summary>
    public static ImageSource? WindowsIcon => _windowsIcon.Value;

    /// <summary>Contextual fallback: gear/service icon.</summary>
    public static ImageSource? GearIcon => _gearIcon.Value;

    /// <summary>Clears the icon cache.</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>Number of cached icons (for diagnostics/testing).</summary>
    public static int CacheCount => _cache.Count;

    // ── Core extraction ──

    private static ImageSource? ExtractIcon(string path)
    {
        if (!File.Exists(path))
            return _appFallback.Value;

        try
        {
            var shfi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_SMALLICON;
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return _appFallback.Value;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
        catch (ExternalException) { return _appFallback.Value; }
        catch (IOException) { return _appFallback.Value; }
    }

    /// <summary>
    /// Extracts a specific icon by index from shell32.dll.
    /// Used for contextual fallback icons (app=2, gear=15, windows=44).
    /// </summary>
    private static ImageSource? ExtractShell32Icon(int index)
    {
        try
        {
            var shell32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");

            var hIcon = ExtractIconW(IntPtr.Zero, shell32, index);
            if (hIcon == IntPtr.Zero || hIcon == (IntPtr)1)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch (ExternalException) { return null; }
        catch (IOException) { return null; }
    }

    // ── Path normalization (aggressive multi-strategy) ──

    internal static string NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        // Handle rundll32 — extract the DLL being called
        if (expanded.Contains("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var dllPath = ExtractRundll32Target(expanded);
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                return dllPath;
        }

        // Handle msiexec — use msiexec.exe itself for icon
        if (expanded.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var msi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            if (File.Exists(msi)) return msi;
        }

        // Try the full expanded string as-is (strip quotes)
        var clean = expanded.Trim('"');
        if (File.Exists(clean))
            return clean;

        // Try extracting from quoted command: "C:\path\app.exe" /args
        if (trimmed.StartsWith('"'))
        {
            var closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote > 1)
            {
                var candidate = Environment.ExpandEnvironmentVariables(trimmed[1..closeQuote]);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Split on common argument patterns
        foreach (var sep in new[] { " /", " -", " --", ",," })
        {
            var idx = clean.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var candidate = clean[..idx].Trim('"', ' ');
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Progressive space-split
        var parts = clean.Split(' ');
        var building = "";
        foreach (var part in parts)
        {
            building = string.IsNullOrEmpty(building) ? part : building + " " + part;
            var candidate = building.Trim('"', ' ');
            if (File.Exists(candidate))
                return candidate;
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        // Search in PATH and common directories
        var exeName = Path.GetFileName(clean.Split(' ')[0].Trim('"'));
        if (!string.IsNullOrEmpty(exeName))
        {
            var found = SearchInPath(exeName);
            if (found != null) return found;
        }

        return clean;
    }

    // ── Helper methods ──

    private static string? ExtractRundll32Target(string command)
    {
        var idx = command.IndexOf("rundll32", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var afterRundll = command[(idx + 8)..].TrimStart();
        if (afterRundll.StartsWith(".exe", StringComparison.OrdinalIgnoreCase))
            afterRundll = afterRundll[4..].TrimStart();

        var commaIdx = afterRundll.IndexOf(',');
        var dllPart = commaIdx > 0 ? afterRundll[..commaIdx].Trim('"', ' ') : afterRundll.Trim('"', ' ');

        if (File.Exists(dllPart))
            return dllPart;

        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var fullPath = Path.Combine(sys32, dllPart);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string? SearchInPath(string exeName)
    {
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        var commonDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        foreach (var dir in commonDirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        return null;
    }

    private static string? FindExecutableByName(string processName)
    {
        var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName : processName + ".exe";

        var found = SearchInPath(exeName);
        if (found != null) return found;

        // Search one level deep in Program Files
        var programDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        foreach (var baseDir in programDirs)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var subDir in Directory.GetDirectories(baseDir))
                {
                    var candidate = Path.Combine(subDir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        // Check App Paths registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var regPath = key?.GetValue("")?.ToString();
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(regPath.Trim('"'));
                if (File.Exists(expanded)) return expanded;
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    // ── System process detection ──

    private static readonly HashSet<string> WindowsSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "fontdrvhost", "dwm", "LogonUI",
        "sihost", "taskhostw", "explorer", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchIndexer",
        "RuntimeBroker", "ApplicationFrameHost", "SystemSettings",
        "TextInputHost", "ctfmon", "conhost", "dllhost", "WmiPrvSE",
        "spoolsv", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "MemCompression",
        "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp",
        "UserOOBEBroker", "WidgetService", "Widgets",
        "PhoneExperienceHost", "LockApp", "CompPkgSrv",
    };

    private static readonly HashSet<string> ServiceProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "msdtc", "wuauserv", "WSearch", "Spooler",
        "AudioSrv", "Audiosrv", "BrokerInfrastructure", "CryptSvc",
        "Dhcp", "Dnscache", "EventLog", "gpsvc", "IKEEXT",
        "iphlpsvc", "LanmanServer", "LanmanWorkstation", "lmhosts",
        "mpssvc", "netprofm", "NlaSvc", "nsi", "PcaSvc",
        "PlugPlay", "Power", "ProfSvc", "RpcEptMapper", "RpcSs",
        "SamSs", "Schedule", "SENS", "SessionEnv", "ShellHWDetection",
        "Themes", "TrkWks", "UserManager", "UsoSvc", "Wcmsvc",
        "WinDefend", "Winmgmt", "WlanSvc", "WpnService", "wscsvc",
        "WdiServiceHost", "WdiSystemHost", "TabletInputService",
        "CDPSvc", "CDPUserSvc", "cbdhsvc", "camsvc",
    };

    internal static bool IsWindowsSystemProcess(string name)
        => WindowsSystemProcesses.Contains(name);

    internal static bool IsServiceProcess(string name)
        => ServiceProcesses.Contains(name) ||
           name.StartsWith("svchost", StringComparison.OrdinalIgnoreCase);

    // ── P/Invoke declarations ──

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
