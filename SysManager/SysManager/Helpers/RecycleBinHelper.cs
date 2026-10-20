// SysManager · RecycleBinHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Single source of truth for measuring and emptying the Windows Recycle Bin via the shell API.
/// Quick Cleanup, Deep Cleanup and the One-Click Tune-Up reach the bin through <c>IRecycleBin</c>,
/// which calls here, so the <c>SHQueryRecycleBin</c> and <c>SHEmptyRecycleBin</c> P/Invokes and the
/// HRESULT handling cannot drift between copies. Uses the shell API rather than
/// <c>Clear-RecycleBin</c> so ghosted entries are removed reliably.
/// </summary>
public static partial class RecycleBinHelper
{
    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
    private const uint SuppressAllFlags = 0x00000007;
    private const uint ErrorNoMoreFiles = 0x80070012; // bin already empty

    /// <summary>
    /// What the current user's Recycle Bin holds on every drive, as the shell counts it, or null when the shell
    /// cannot say.
    /// </summary>
    /// <remarks>
    /// The shell's own figures: what Explorer shows, and what <see cref="Empty()"/> then empties. Walking the bin's
    /// folders, as Deep Cleanup and Quick Cleanup did, counted the shell's bookkeeping with the contents: the
    /// <c>desktop.ini</c> each folder holds even when the bin is empty, and the <c>$I</c> index file beside each
    /// deleted item. So an empty bin read as one file of 129 bytes, and a bin of N files as 2N + 1. A deleted folder
    /// is one item here, as in Explorer, however many files it holds.
    /// <para>It reads one small index file per item rather than every file under a deleted folder, so it is also much
    /// quicker on a full bin. It still reads every drive, so callers keep it off the UI thread.</para>
    /// </remarks>
    public static RecycleBinContents? Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        // An HRESULT, like SHEmptyRecycleBin's: a failure comes back as a code, never as an exception.
        int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        if (hr < 0)
        {
            Log.Warning("Recycle Bin query failed: HRESULT 0x{Hr:X8}", hr);
            return null;
        }
        return new RecycleBinContents(info.i64Size, info.i64NumItems);
    }

    /// <summary>
    /// Empties the Recycle Bin on all drives with no confirmation, progress or sound, and reports what it held just
    /// before.
    /// </summary>
    public static RecycleBinEmptyResult Empty() =>
        Empty(Query, () => NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, SuppressAllFlags));

    /// <summary>
    /// What <see cref="Empty()"/> decides, handed the two shell calls so that it can be tested without touching the
    /// bin of the PC running the tests.
    /// </summary>
    /// <remarks>
    /// The bin is asked first, and left alone when it holds nothing. What <c>SHEmptyRecycleBin</c> returns for an
    /// empty bin is not documented, and every code but success and <c>ERROR_NO_MORE_FILES</c> used to count as a
    /// refusal, so emptying a bin that held nothing could end in "Windows refused the request" (#2800).
    /// <para>A failure code is then judged by the bin rather than by the code: when the bin is empty afterwards, the
    /// user has what they asked for, whichever code the shell returned.</para>
    /// </remarks>
    internal static RecycleBinEmptyResult Empty(Func<RecycleBinContents?> query, Func<int> emptyAll)
    {
        var before = query();
        if (before is { Items: 0 }) return new RecycleBinEmptyResult(Emptied: true, before);

        // SHEmptyRecycleBin is a LibraryImport returning an HRESULT — it reports failure
        // through the return code, not an exception, so there is nothing to catch here.
        int hr = emptyAll();
        if (hr >= 0 || unchecked((uint)hr) == ErrorNoMoreFiles || query() is { Items: 0 })
            return new RecycleBinEmptyResult(Emptied: true, before);

        Log.Warning("Empty recycle bin failed: HRESULT 0x{Hr:X8}", hr);
        return new RecycleBinEmptyResult(Emptied: false, before);
    }

    /// <summary>
    /// The per-drive Recycle Bin folders that belong to the CURRENT user, i.e.
    /// <c>&lt;drive&gt;\$Recycle.Bin\&lt;current-SID&gt;</c> on every fixed, ready drive: where Deep
    /// Cleanup's Recycle Bin category says the bin lives. Its size does not come from walking them but
    /// from <see cref="Query"/>, the shell's count of the same user's bin, which <see cref="Empty()"/>
    /// then empties; the whole <c>$Recycle.Bin</c> tree would include other users' bins, which it
    /// leaves alone. Returns an empty array when the current SID can't be resolved.
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

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SHQueryRecycleBinW")]
        internal static partial int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        /// <summary>
        /// <c>SHQUERYRBINFO</c> as 64-bit Windows lays it out, the only Windows SysManager is built for: the size,
        /// four bytes of padding, then the two 64-bit counts, 24 bytes in all. <c>shellapi.h</c> packs it to one byte
        /// on 32-bit Windows, where the size would not match and the query would report that it could not say.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }
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

/// <summary>What the current user's Recycle Bin holds on every drive, as the shell counts it.</summary>
/// <param name="Bytes">The size of what is in it.</param>
/// <param name="Items">The deleted files and folders in it. A folder is one item, as in Explorer.</param>
public readonly record struct RecycleBinContents(long Bytes, long Items)
{
    /// <summary>"3 items, 1.2 GB", for a sentence that says what was emptied.</summary>
    public string Display => string.Create(CultureInfo.InvariantCulture,
        $"{Items:N0} item{(Items == 1 ? "" : "s")}, {FormatHelper.FormatSize(Bytes)}");
}

/// <summary>How emptying the Recycle Bin went.</summary>
/// <param name="Emptied">True when the bin is empty now, emptied here or already empty.</param>
/// <param name="Before">What the shell said the bin held just before, or null when it could not say.</param>
public readonly record struct RecycleBinEmptyResult(bool Emptied, RecycleBinContents? Before)
{
    /// <summary>True when the bin held nothing, so the shell was not asked to empty it.</summary>
    public bool WasAlreadyEmpty => Before is { Items: 0 };
}
