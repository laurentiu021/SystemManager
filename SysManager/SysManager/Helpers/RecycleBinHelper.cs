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
        try
        {
            // WindowsIdentity owns a native token handle — dispose it deterministically
            // (matches AdminHelper.IsElevated / App.xaml.cs) instead of waiting for the finalizer.
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
        }
        catch (System.Security.SecurityException) { sid = null; }
        if (string.IsNullOrEmpty(sid)) return [];

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid))
            .ToArray();
    }

    /// <summary>
    /// What <see cref="SendToRecycleBin"/> asks the shell for: recycle rather than delete, without the "are you sure"
    /// the app has already asked in its own words, and with a warning before anything is deleted for good.
    /// </summary>
    /// <remarks>
    /// <c>FOF_WANTNUKEWARNING</c> is the part that keeps the promise both callers make, that what they remove can be
    /// put back. The shell cannot recycle an item bigger than the drive's Recycle Bin, or anything on a drive whose
    /// bin is turned off, and under <c>FOF_NOCONFIRMATION</c> alone it then deletes it permanently without a word.
    /// With the flag it asks first; declining leaves the item where it was and counts as not removed (#1527).
    /// </remarks>
    internal const ushort RecycleFlags =
        ShellFileOperation.FOF_ALLOWUNDO | ShellFileOperation.FOF_NOCONFIRMATION | ShellFileOperation.FOF_WANTNUKEWARNING;

    /// <summary>
    /// Sends one file or folder to the Recycle Bin through the shell, so it can be put back from there. Returns true
    /// only when the shell reports it done.
    /// </summary>
    /// <remarks>
    /// One copy for Shortcut Cleaner and the Uninstaller's leftovers (#1527), which had it privately before the second
    /// caller came. <c>SHFileOperation</c> reports failure through its return code and
    /// <c>fAnyOperationsAborted</c>, never by throwing, so both are read: a recycle that silently did nothing must
    /// not be counted as removed. It runs synchronously and can take a while for a large folder, so callers keep it
    /// off the UI thread.
    /// </remarks>
    public static bool SendToRecycleBin(string path)
    {
        var op = new ShellFileOperation.SHFILEOPSTRUCT
        {
            wFunc = ShellFileOperation.FO_DELETE,
            pFrom = path + '\0' + '\0',
            fFlags = RecycleFlags,
        };
        var rc = ShellFileOperation.SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SHEmptyRecycleBinW")]
        internal static partial int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }

    /// <summary>
    /// <c>SHFileOperation</c> stays a classic <c>[DllImport]</c>: its struct carries strings, which
    /// <c>[LibraryImport]</c> would need a hand-written marshaller for.
    /// </summary>
    private static class ShellFileOperation
    {
        internal const uint FO_DELETE = 0x0003;
        internal const ushort FOF_NOCONFIRMATION = 0x0010;
        internal const ushort FOF_ALLOWUNDO = 0x0040;
        internal const ushort FOF_WANTNUKEWARNING = 0x4000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }
    }
}
