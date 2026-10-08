// SysManager · TempLeftoverEnvironment
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The folders and registry a leftover search reads, pointed at a temp tree the test owns and deletes, with the registry
/// replaced by a set of key paths and a list of uninstall entries.
/// </summary>
/// <remarks>
/// Every root is its own folder, as in <see cref="TempCleanupRoots"/>, so a test decides exactly what exists where, and
/// the reparse-point and final-path checks run against real directories. Nothing here reads or writes the real
/// registry or any folder of the PC running the suite. The temp tree's drive reports itself fixed whatever it really
/// is, so the outcome never depends on where the machine running the suite keeps its temp folder.
/// </remarks>
public sealed class TempLeftoverEnvironment : ILeftoverEnvironment, IDisposable
{
    /// <summary>
    /// The tree everything lives in, deleted on dispose. In its long form: a runner's temp folder can be an 8.3 name
    /// such as <c>RUNNER~1</c>, and the search reports every path with short names expanded.
    /// </summary>
    public string Root { get; }

    public string RoamingAppData { get; }
    public string LocalAppData { get; }
    public string ProgramData { get; }
    public string UserProfile { get; }
    public string WindowsDirectory { get; }
    public string ProgramFiles { get; }
    public string ProgramFilesX86 { get; }
    public string Documents { get; }
    public IReadOnlyList<(string Name, string Path)> UserFolders { get; }
    public IReadOnlyList<string> OwnFolders { get; }

    /// <summary>What <see cref="RegistryKeyExists"/> answers yes to, as paths under <c>HKEY_CURRENT_USER</c>.</summary>
    public HashSet<string> RegistryKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="RegisteredApps"/> reports: the uninstall entries Windows would hold.</summary>
    public List<UninstallProbe> Registered { get; } = [];

    /// <summary>The kind of drive each root reports. The temp tree's own root starts out fixed.</summary>
    public Dictionary<string, DriveType> DriveTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsElevated { get; set; }

    public TempLeftoverEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLeftovers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Root = LeftoverFinder.Normalize(root) ?? root;
        UserProfile = Sub(@"Users\me");
        RoamingAppData = Sub(@"Users\me\AppData\Roaming");
        LocalAppData = Sub(@"Users\me\AppData\Local");
        Documents = Sub(@"Users\me\Documents");
        ProgramData = Sub("ProgramData");
        WindowsDirectory = Sub("Windows");
        ProgramFiles = Sub("Program Files");
        ProgramFilesX86 = Sub("Program Files (x86)");
        UserFolders = [("Documents", Documents), ("Desktop", Sub(@"Users\me\Desktop"))];
        OwnFolders = [Path.Combine(LocalAppData, "SysManager")];
        DriveTypes[Path.GetPathRoot(Root)!] = DriveType.Fixed;
    }

    public bool RegistryKeyExists(string currentUserSubKey) => RegistryKeys.Contains(currentUserSubKey);

    public IReadOnlyList<UninstallProbe> RegisteredApps() => Registered;

    /// <summary>A root nobody set reports <see cref="DriveType.NoRootDirectory"/>, which is never this PC's own drive.</summary>
    public DriveType DriveTypeOf(string root) =>
        DriveTypes.TryGetValue(root, out var type) ? type : DriveType.NoRootDirectory;

    /// <summary>Creates <paramref name="path"/> and puts a file of <paramref name="bytes"/> bytes in it; returns the path.</summary>
    public static string Folder(string path, int bytes = 0)
    {
        Directory.CreateDirectory(path);
        if (bytes > 0) File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[bytes]);
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
