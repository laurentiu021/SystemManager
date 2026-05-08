// SysManager · KnownFolders
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;

namespace SysManager.Helpers;

/// <summary>
/// Resolves Windows Known Folder paths using SHGetKnownFolderPath.
/// Handles cases where the user has moved default folders (Downloads,
/// Documents, Desktop, etc.) to a non-standard location.
/// </summary>
internal static class KnownFolders
{
    // Known Folder GUIDs — https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        nint hToken,
        out string pszPath);

    /// <summary>Gets the actual Downloads folder path (respects user relocation).</summary>
    public static string GetDownloadsPath() => GetPath(Downloads);

    /// <summary>Gets the actual Documents folder path (respects user relocation).</summary>
    public static string GetDocumentsPath() => GetPath(Documents);

    /// <summary>Gets the actual Desktop folder path (respects user relocation).</summary>
    public static string GetDesktopPath() => GetPath(Desktop);

    /// <summary>Gets the actual Pictures folder path (respects user relocation).</summary>
    public static string GetPicturesPath() => GetPath(Pictures);

    /// <summary>Gets the actual Music folder path (respects user relocation).</summary>
    public static string GetMusicPath() => GetPath(Music);

    /// <summary>Gets the actual Videos folder path (respects user relocation).</summary>
    public static string GetVideosPath() => GetPath(Videos);

    private static string GetPath(Guid folderId)
    {
        try
        {
            SHGetKnownFolderPath(folderId, 0, nint.Zero, out var path);
            return path;
        }
        catch (COMException)
        {
            // Fallback to Environment.SpecialFolder if P/Invoke fails
            return folderId == Downloads
                ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : folderId == Documents
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : folderId == Desktop
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : folderId == Pictures
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                : folderId == Music
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                : folderId == Videos
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                : string.Empty;
        }
    }
}
