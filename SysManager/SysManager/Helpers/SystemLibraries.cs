// SysManager · SystemLibraries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SysManager.Helpers;

/// <summary>Loads every library SysManager's own native calls name from System32, by its full path.</summary>
/// <remarks>
/// Every library this assembly imports is a Windows DLL in System32, and SysManager runs elevated from a folder the
/// user's account can write to. Only <c>dismapi.dll</c> was pinned to System32; the rest (pdh, dxgi, wtsapi32,
/// rstrtmgr, powrprof, wintrust, iphlpapi, dwmapi and others) were looked for in the exe's folder first, and pdh is
/// opened at every start. The assembly-wide <see cref="DefaultDllImportSearchPathsAttribute"/> in AssemblyInfo is the
/// declarative half. It is not enough on its own: the runtime looks in the folders the single-file host names
/// (the unpacking folder under the user's temp folder) before it applies a search-path flag, and a resolver runs
/// before any of that.
/// <para>Registered by a module initializer, so it is in place before any code of this assembly runs, App's
/// construction and resources included. It touches nothing else of SysManager's: it runs inside the first native call,
/// and another class's static initializer would run inside it.</para>
/// <para>Libraries that a Windows DLL loads for itself, and the native files the host and WPF load, are outside it:
/// this decides only the libraries SysManager's own declarations name.</para>
/// </remarks>
internal static class SystemLibraries
{
    /// <summary>Hands this assembly's imports to <see cref="Resolve"/>, once, when the assembly loads.</summary>
    [ModuleInitializer]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(SystemLibraries).Assembly,
            (name, _, _) => Resolve(name, Environment.SystemDirectory, NativeLibrary.Load));

    /// <summary>
    /// The library <paramref name="libraryName"/> loaded from <paramref name="systemDirectory"/> through
    /// <paramref name="load"/>. Never 0, which would let the runtime search the exe's folder and the unpacking folder
    /// after all: a name that carries a folder is refused instead.
    /// </summary>
    internal static nint Resolve(string libraryName, string systemDirectory, Func<string, nint> load)
        => ResolvePath(libraryName, systemDirectory) is { } path
            ? load(path)
            : throw new DllNotFoundException($"'{libraryName}' is not a bare library name, so it is not loaded.");

    /// <summary>
    /// The full path of the bare library name <paramref name="libraryName"/> in
    /// <paramref name="systemDirectory"/>, with ".dll" added when it has no extension, or null for a name that is
    /// blank or carries a folder.
    /// </summary>
    internal static string? ResolvePath(string libraryName, string systemDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryName) || Path.IsPathRooted(libraryName)
            || libraryName.Contains('\\') || libraryName.Contains('/')) return null;
        return Path.Join(systemDirectory, Path.HasExtension(libraryName) ? libraryName : libraryName + ".dll");
    }
}
