// SysManager · UnopenableFolder
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Tests;

/// <summary>
/// A folder inside a walked tree that the walk finds but cannot open: its name ends in a dot, which only a
/// <c>\\?\</c> path can create, and every ordinary path drops the dot, so listing it fails as listing a folder with
/// no right to read it does.
/// </summary>
/// <remarks>
/// A deny entry on a real folder would be the obvious way, and it does not work wherever the suite runs with backup
/// privileges enabled: the walk opens folders with backup semantics, and the deny is passed by. This fails the same
/// way for every account. Delete it by disposing, before the tree it sits in: an ordinary recursive delete cannot
/// reach it either.
/// </remarks>
internal sealed class UnopenableFolder : IDisposable
{
    private const string Prefix = @"\\?\";

    /// <summary>The folder as the walk names it: the ordinary path, with the dot it cannot open.</summary>
    public string OrdinaryPath { get; }

    private UnopenableFolder(string ordinaryPath) => OrdinaryPath = ordinaryPath;

    /// <summary>Makes one in <paramref name="parent"/>, and checks that an ordinary path cannot list it.</summary>
    public static UnopenableFolder In(string parent)
    {
        var folder = new UnopenableFolder(Path.Combine(parent, "inside."));
        Directory.CreateDirectory(Prefix + folder.OrdinaryPath);
        Assert.True(Directory.Exists(Prefix + folder.OrdinaryPath), $"{folder.OrdinaryPath} was not created.");
        Assert.ThrowsAny<IOException>(() => Directory.GetFiles(folder.OrdinaryPath));
        return folder;
    }

    public void Dispose()
    {
        try { Directory.Delete(Prefix + OrdinaryPath); }
        catch (IOException) { /* left in a temp tree: harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }
}
