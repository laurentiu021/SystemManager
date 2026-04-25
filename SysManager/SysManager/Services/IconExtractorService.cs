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

namespace SysManager.Services;

/// <summary>
/// Extracts application icons from executable files using the Windows Shell API.
/// Results are cached to avoid repeated extraction on refresh.
/// Falls back to a generic application icon when extraction fails.
/// </summary>
public sealed class IconExtractorService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<ImageSource?> _fallbackIcon = new(CreateFallbackIcon);

    /// <summary>
    /// Gets the icon for the given executable path. Returns a cached result
    /// if available, otherwise extracts and caches it. Thread-safe.
    /// </summary>
    public static ImageSource? GetIcon(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return _fallbackIcon.Value;

        var normalized = NormalizePath(filePath);
        if (string.IsNullOrEmpty(normalized))
            return _fallbackIcon.Value;

        return _cache.GetOrAdd(normalized, static path => ExtractIcon(path));
    }

    /// <summary>Clears the icon cache (e.g. on full refresh).</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>Number of cached icons (for diagnostics/testing).</summary>
    public static int CacheCount => _cache.Count;

    /// <summary>The generic fallback icon used when extraction fails.</summary>
    public static ImageSource? FallbackIcon => _fallbackIcon.Value;

    private static ImageSource? ExtractIcon(string path)
    {
        if (!File.Exists(path))
            return _fallbackIcon.Value;

        try
        {
            var shfi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_SMALLICON;
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return _fallbackIcon.Value;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
        catch (ExternalException)
        {
            return _fallbackIcon.Value;
        }
        catch (IOException)
        {
            return _fallbackIcon.Value;
        }
    }

    /// <summary>
    /// Normalizes a command-line string to an executable path.
    /// Handles quoted paths, paths with arguments, environment variables.
    /// </summary>
    internal static string NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var path = Environment.ExpandEnvironmentVariables(raw.Trim());

        // Strip quotes
        path = path.Trim('"');

        // If the full string is a valid file, use it
        if (File.Exists(path))
            return path;

        // Try to extract exe from quoted command: "C:\path\app.exe" /arg
        if (raw.TrimStart().StartsWith('"'))
        {
            var inner = raw.TrimStart()[1..];
            var closeQuote = inner.IndexOf('"');
            if (closeQuote > 0)
            {
                var candidate = Environment.ExpandEnvironmentVariables(inner[..closeQuote]);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Split on common argument separators
        var separators = new[] { " /", " -", " --" };
        foreach (var sep in separators)
        {
            var idx = path.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var candidate = path[..idx].Trim('"', ' ');
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Last resort: split on space and try progressively longer paths
        var parts = path.Split(' ');
        var building = "";
        foreach (var part in parts)
        {
            building = string.IsNullOrEmpty(building) ? part : building + " " + part;
            var trimmed = building.Trim('"', ' ');
            if (File.Exists(trimmed))
                return trimmed;
        }

        return path;
    }

    private static ImageSource? CreateFallbackIcon()
    {
        try
        {
            var shell32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "shell32.dll");

            var shfi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_SMALLICON;
            var result = SHGetFileInfo(shell32, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }
        }
        catch (ExternalException) { }
        catch (IOException) { }

        return null;
    }

    // --- P/Invoke declarations ---

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
