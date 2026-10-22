// SysManager · FileIdentity — which file a handle is open on, the same for every name the file has
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysManager.Helpers;

/// <summary>
/// Which file a handle is open on: the volume it is on and its ID there. Every hard link to a file has the same
/// identity, so two names with one identity are one file, not two copies of it.
/// </summary>
/// <remarks>
/// The ID is the 128-bit one Windows keeps for every file system. The 64-bit index that
/// <c>GetFileInformationByHandle</c> returns is not unique on ReFS, where two different files can share it.
/// </remarks>
/// <param name="VolumeSerialNumber">The serial number of the volume the file is on.</param>
/// <param name="FileId">The file's ID on that volume.</param>
internal readonly partial record struct FileIdentity(ulong VolumeSerialNumber, UInt128 FileId)
{
    /// <summary>
    /// The identity of the file <paramref name="handle"/> is open on, or null when Windows does not say.
    /// </summary>
    internal static FileIdentity? Of(SafeFileHandle handle) =>
        NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfoClass, out var info,
            (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>())
            ? new FileIdentity(info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow))
            : null;

    /// <summary>
    /// How many names the file <paramref name="handle"/> is open on has, or null when Windows does not say.
    /// </summary>
    internal static uint? LinksOf(SafeFileHandle handle) =>
        NativeMethods.GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : null;

    private static partial class NativeMethods
    {
        /// <summary><c>FileIdInfo</c> in <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
        internal const int FileIdInfoClass = 18;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            int fileInformationClass,
            out FileIdInfo lpFileInformation,
            uint dwBufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        // FILE_ID_INFO: a 64-bit volume serial number, then the 16 bytes of FILE_ID_128, read here as two 64-bit
        // halves, low first, as the bytes are laid out. 24 bytes, blittable.
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInfo
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        // Layout must match the native BY_HANDLE_FILE_INFORMATION exactly. All fields are plain
        // 4-byte DWORDs — each FILETIME is two DWORDs, expanded here — so the struct is fully
        // blittable. LibraryImport rejects a struct that needs runtime marshalling (SYSLIB1051),
        // and enabling assembly-wide DisableRuntimeMarshalling would break the classic
        // [DllImport] surfaces that DO rely on it. Expanding keeps NumberOfLinks at its native
        // offset (40) with no padding.
        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
