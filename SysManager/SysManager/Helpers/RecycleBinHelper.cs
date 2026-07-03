// SysManager · RecycleBinHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Single source of truth for emptying the Windows Recycle Bin via the shell API.
/// Both Deep Cleanup and the One-Click Tune-Up empty the bin; routing them through one
/// helper keeps the <c>SHEmptyRecycleBin</c> P/Invoke and the HRESULT handling from
/// drifting between copies. Uses the shell API rather than <c>Clear-RecycleBin</c> so
/// ghosted entries are removed reliably.
/// </summary>
public static partial class RecycleBinHelper
{
    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
    private const uint SuppressAllFlags = 0x00000007;
    private const uint ErrorNoMoreFiles = 0x80070012; // bin already empty

    /// <summary>
    /// Empties the Recycle Bin on all drives with no confirmation/progress/sound.
    /// Returns true on success (or when the bin is already empty).
    /// </summary>
    public static bool EmptyAllDrives()
    {
        // SHEmptyRecycleBin is a LibraryImport returning an HRESULT — it reports failure
        // through the return code, not an exception, so there is nothing to catch here.
        int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, SuppressAllFlags);
        // S_OK (>= 0) = success, ERROR_NO_MORE_FILES = bin already empty.
        bool ok = hr >= 0 || unchecked((uint)hr) == ErrorNoMoreFiles;
        if (!ok)
            Log.Warning("Empty recycle bin failed: HRESULT 0x{Hr:X8}", hr);
        return ok;
    }

    /// <summary>
    /// The per-drive Recycle Bin folders that belong to the CURRENT user, i.e.
    /// <c>&lt;drive&gt;\$Recycle.Bin\&lt;current-SID&gt;</c> on every fixed, ready drive. Sizing must
    /// use these — not the whole <c>$Recycle.Bin</c> tree — because <see cref="EmptyAllDrives"/>
    /// (SHEmptyRecycleBin) only empties the calling user's bin; summing all SIDs over-reports the
    /// freeable bytes on a multi-user machine (especially when elevated, where the other users'
    /// folders become readable). Returns an empty array when the current SID can't be resolved.
    /// </summary>
    public static string[] CurrentUserBinPaths()
    {
        string? sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; }
        catch (System.Security.SecurityException) { sid = null; }
        if (string.IsNullOrEmpty(sid)) return [];

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid))
            .ToArray();
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SHEmptyRecycleBinW")]
        internal static partial int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }
}
