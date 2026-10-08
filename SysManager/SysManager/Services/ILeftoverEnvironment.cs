// SysManager · ILeftoverEnvironment
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The folders and the registry an uninstall-leftover search looks in, and the ones it must never offer.
/// </summary>
/// <remarks>
/// A seam for the same reason as <see cref="ICleanupRoots"/>: the search reads real directories, so a test points it
/// at a tree it built and asserts exactly what is offered and what is refused, and the reparse-point and final-path
/// checks run against real folders rather than a fake. The registry is behind <see cref="RegistryKeyExists"/> and
/// <see cref="RegisteredApps"/>, so no test reads or writes the real registry.
/// </remarks>
public interface ILeftoverEnvironment
{
    /// <summary><c>%AppData%</c>.</summary>
    string RoamingAppData { get; }

    /// <summary><c>%LocalAppData%</c>.</summary>
    string LocalAppData { get; }

    /// <summary><c>%ProgramData%</c>.</summary>
    string ProgramData { get; }

    /// <summary>The user's profile folder, <c>C:\Users\&lt;name&gt;</c>.</summary>
    string UserProfile { get; }

    /// <summary>The Windows folder. Nothing inside it is ever offered.</summary>
    string WindowsDirectory { get; }

    string ProgramFiles { get; }

    string ProgramFilesX86 { get; }

    /// <summary>
    /// Documents, Desktop, Downloads, Pictures, Music and Videos, wherever the user has moved them, each with the
    /// name a refusal is worded with. Never offered, nor anything inside them: they hold the user's own files.
    /// </summary>
    IReadOnlyList<(string Name, string Path)> UserFolders { get; }

    /// <summary>SysManager's own folders, never offered.</summary>
    IReadOnlyList<string> OwnFolders { get; }

    /// <summary>Whether SysManager runs as administrator, which decides what it can remove now.</summary>
    bool IsElevated { get; }

    /// <summary>Whether <c>HKEY_CURRENT_USER\<paramref name="currentUserSubKey"/></c> exists.</summary>
    bool RegistryKeyExists(string currentUserSubKey);

    /// <summary>
    /// Every uninstall entry Windows holds, for this user and for the machine, including the ones the Uninstaller's
    /// list hides, such as drivers and runtimes.
    /// </summary>
    /// <remarks>
    /// These decide what is shared. The list on screen leaves out system components, and a folder or a publisher
    /// name a hidden driver package still uses is exactly what must not be offered as a leftover.
    /// </remarks>
    IReadOnlyList<UninstallProbe> RegisteredApps();

    /// <summary>What kind of drive <paramref name="root"/> (for example <c>C:\</c>) is.</summary>
    DriveType DriveTypeOf(string root);
}

/// <summary>The real machine's folders and registry.</summary>
public sealed class SystemLeftoverEnvironment : ILeftoverEnvironment
{
    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string WindowsDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string ProgramFiles { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public IReadOnlyList<(string Name, string Path)> UserFolders { get; } =
    [
        ("Documents", KnownFolders.GetDocumentsPath()),
        ("Desktop", KnownFolders.GetDesktopPath()),
        ("Downloads", KnownFolders.GetDownloadsPath()),
        ("Pictures", KnownFolders.GetPicturesPath()),
        ("Music", KnownFolders.GetMusicPath()),
        ("Videos", KnownFolders.GetVideosPath()),
    ];

    /// <summary>
    /// SysManager's two data folders and the folder the running exe is in, which is where a portable copy keeps
    /// itself and where a single-file build unpacks.
    /// </summary>
    public IReadOnlyList<string> OwnFolders { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager"),
        AppContext.BaseDirectory,
    ];

    public bool IsElevated => AdminHelper.IsElevated();

    public bool RegistryKeyExists(string currentUserSubKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(currentUserSubKey);
            return key is not null;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Every entry <see cref="UninstallEntries"/> reads, hidden components and updates included.</summary>
    public IReadOnlyList<UninstallProbe> RegisteredApps() =>
        [.. UninstallEntries.ReadAll().Entries.Select(e => new UninstallProbe(e.Name, e.Publisher, "", e.InstallLocation, e.UninstallCommand))];

    public DriveType DriveTypeOf(string root)
    {
        try { return new DriveInfo(root).DriveType; }
        catch (ArgumentException) { return DriveType.Unknown; }
    }
}
