// SysManager · EnvVariableTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EnvVariable"/>'s list-classification logic — the gates that
/// drive the PATH list editor and its directory-existence annotation.
/// </summary>
public class EnvVariableTests
{
    private static EnvVariable Make(string name) =>
        new() { Name = name, Scope = EnvVarScope.Machine, Value = "" };

    [Theory]
    [InlineData("Path")]
    [InlineData("PATH")]
    [InlineData("PATHEXT")]
    [InlineData("PSModulePath")]
    public void IsPathLike_TrueForPathVariables(string name)
        => Assert.True(Make(name).IsPathLike);

    [Theory]
    [InlineData("TEMP")]
    [InlineData("USERNAME")]
    [InlineData("OS")]
    public void IsPathLike_FalseForOrdinaryVariables(string name)
        => Assert.False(Make(name).IsPathLike);

    [Theory]
    [InlineData("Path")]
    [InlineData("PATH")]
    [InlineData("PSModulePath")]
    public void IsDirectoryList_TrueForRealDirectoryLists(string name)
        => Assert.True(Make(name).IsDirectoryList);

    [Theory]
    [InlineData("PATHEXT")]
    [InlineData("pathext")]
    public void IsDirectoryList_FalseForPathext(string name)
    {
        // Regression (P2 #12): PATHEXT is a list of file EXTENSIONS (.COM;.EXE;…), not
        // directories. Before the fix, IsPathLike alone gated the PATH editor's
        // Directory.Exists() annotation, so every PATHEXT entry (present in System scope
        // on every Windows install) was flagged IsMissing=true and the whole list
        // rendered red. IsDirectoryList must exclude PATHEXT while IsPathLike still
        // includes it (so the reorderable list editor is still shown).
        var v = Make(name);
        Assert.True(v.IsPathLike, "PATHEXT should still open the list editor");
        Assert.False(v.IsDirectoryList, "PATHEXT entries are extensions, not directories");
    }
}
