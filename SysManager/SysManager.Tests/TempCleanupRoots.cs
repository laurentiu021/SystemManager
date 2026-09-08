// SysManager · TempCleanupRoots
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Deep Cleanup's scan roots, pointed at a temp tree the test owns and deletes.
/// </summary>
/// <remarks>
/// Every root gets its own subfolder, so a category cannot pick up files planted for another — the
/// definitions overlap heavily under <c>%LOCALAPPDATA%</c>, and a shared folder would make an exact
/// count depend on which categories happen to look there.
/// <para><c>FixedDriveRoots</c> and <c>RecycleBinPaths</c> are empty deliberately. A real drive root here
/// would put the Steam, shader-cache and Riot probes back on the machine, and the real bins are what made
/// the first version of these tests take 48 seconds instead of 0.2 (#2176).</para>
/// <para>Shared rather than nested in one test class because two consumers need it: the deterministic
/// scan-logic tests, and the class fixture that the catalogue tests share. Copying it would put the "which
/// roots are redirected" decision in two places, which is the drift #2183 documents.</para>
/// </remarks>
public sealed class TempCleanupRoots : ICleanupRoots, IDisposable
{
    /// <summary>The tree everything below lives in — deleted on dispose.</summary>
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "SysManagerScanRoots", Guid.NewGuid().ToString("N"));

    public string LocalAppData { get; }
    public string ProgramData { get; }
    public string SystemDrive { get; }
    public string WindowsDirectory { get; }
    public string UserTemp { get; }
    public string ProgramFilesX86 { get; }
    public string ProgramFiles { get; }

    /// <summary>Empty: a real drive root would put the launcher probes back on the machine.</summary>
    public IReadOnlyList<string> FixedDriveRoots { get; } = [];

    /// <summary>Empty: the real bins are what made these tests take 48 seconds instead of 0.2.</summary>
    public IReadOnlyList<string> RecycleBinPaths { get; } = [];

    public TempCleanupRoots()
    {
        LocalAppData = Sub("LocalAppData");
        ProgramData = Sub("ProgramData");
        SystemDrive = Sub("SystemDrive");
        WindowsDirectory = Sub("Windows");
        UserTemp = Sub("Temp");
        ProgramFilesX86 = Sub("ProgramFilesX86");
        ProgramFiles = Sub("ProgramFiles");
    }

    /// <summary>
    /// Writes a file of <paramref name="bytes"/> zero bytes at <paramref name="path"/>, creating its
    /// directory, and returns the path.
    /// </summary>
    public static string WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a leftover tree under %TEMP% is harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    private string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
