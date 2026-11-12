// SysManager · SystemLibrariesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="SystemLibraries"/> loads each library SysManager's own imports name from System32 by its full path, and
/// refuses a name that carries a folder rather than handing it back to the runtime's search.
/// </summary>
public class SystemLibrariesTests
{
    private const string System32 = @"X:\Win\System32";

    [Theory]
    [InlineData("pdh.dll", @"X:\Win\System32\pdh.dll")]
    [InlineData("PDH.DLL", @"X:\Win\System32\PDH.DLL")]
    [InlineData("pdh", @"X:\Win\System32\pdh.dll")]
    public void ResolvePath_ABareLibraryName_IsTheFileInTheSystemFolder(string name, string expected)
        => Assert.Equal(expected, SystemLibraries.ResolvePath(name, System32));

    [Theory]
    [InlineData(@"C:\Users\u\Downloads\pdh.dll")]
    [InlineData(@"sub\pdh.dll")]
    [InlineData("sub/pdh.dll")]
    [InlineData(@"\\server\share\pdh.dll")]
    [InlineData(@"..\pdh.dll")]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolvePath_ANameThatCarriesAFolder_IsRefused(string name)
        => Assert.Null(SystemLibraries.ResolvePath(name, System32));

    [Fact]
    public void Resolve_LoadsOnlyTheSystemFolderCopy_AndARefusedNameFailsClosed()
    {
        List<string> loaded = [];
        nint Load(string path)
        {
            loaded.Add(path);
            return 42;
        }

        Assert.Equal(42, SystemLibraries.Resolve("pdh.dll", System32, Load));
        Assert.Equal([@"X:\Win\System32\pdh.dll"], loaded);

        // Never 0: that would hand the name back to the runtime, which searches the exe's folder first.
        Assert.Throws<DllNotFoundException>(() => SystemLibraries.Resolve(@"C:\Downloads\pdh.dll", System32, Load));
        Assert.Single(loaded);
    }
}
