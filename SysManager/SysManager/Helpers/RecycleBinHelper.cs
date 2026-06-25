// SysManager · RecycleBinHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
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
        try
        {
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, SuppressAllFlags);
            // S_OK (>= 0) = success, ERROR_NO_MORE_FILES = bin already empty.
            return hr >= 0 || unchecked((uint)hr) == ErrorNoMoreFiles;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Empty recycle bin failed: {Error}", ex.Message);
            return false;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SHEmptyRecycleBinW")]
        internal static partial int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }
}
