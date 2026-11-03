// SysManager · InstalledWindows
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// The edition and build of the Windows SysManager runs on, as the registry names them under
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c>: <c>EditionID</c> (<c>Core</c> for Home,
/// <c>Professional</c>, <c>Enterprise</c>, …) and <c>CurrentBuildNumber</c>. Read under the HKLM root a service was
/// given, so a test chooses both through the seam the service already has (#2778).
/// </summary>
/// <param name="EditionId">The edition, or null when it could not be read.</param>
/// <param name="Build">The build number, or null when it could not be read.</param>
internal readonly record struct InstalledWindows(string? EditionId, int? Build)
{
    /// <summary>The key both values are read from, below the HKLM root.</summary>
    internal const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>The first build of Windows 11.</summary>
    internal const int FirstWindows11Build = 22000;

    /// <summary>
    /// True on Windows 10, whose news and interests a different policy turns off than Windows 11's widgets. A build
    /// that could not be read counts as Windows 11, which every switch assumed before the build was read.
    /// </summary>
    internal bool IsWindows10 => Build is < FirstWindows11Build;

    /// <summary>The edition and build under <paramref name="hklmRoot"/>; null for either that cannot be read.</summary>
    internal static InstalledWindows Read(RegistryKey hklmRoot)
    {
        ArgumentNullException.ThrowIfNull(hklmRoot);
        try
        {
            using var key = hklmRoot.OpenSubKey(CurrentVersionPath, writable: false);
            if (key is null) return default;

            return new InstalledWindows(
                key.GetValue("EditionID") as string,
                int.TryParse(key.GetValue("CurrentBuildNumber") as string, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var build) ? build : null);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Cannot read the Windows edition and build — access denied: {Error}", ex.Message);
            return default;
        }
        catch (SecurityException ex)
        {
            Log.Debug("Cannot read the Windows edition and build — security exception: {Error}", ex.Message);
            return default;
        }
        catch (IOException ex)
        {
            Log.Debug("Cannot read the Windows edition and build — registry I/O error: {Error}", ex.Message);
            return default;
        }
    }
}
