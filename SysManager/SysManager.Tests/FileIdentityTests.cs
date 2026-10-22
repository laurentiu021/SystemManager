// SysManager · FileIdentityTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="FileIdentity"/> tells two names of one file from two copies of it, which Duplicate Finder needs to leave
/// hard links out of its groups and File Shredder needs to refuse a file whose data other names share (#2823).
/// </summary>
public sealed class FileIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SysManagerFileId_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a scanner still holding a file; the temp folder is cleared later */ }
        catch (UnauthorizedAccessException) { /* the same, for a file marked read-only by something else */ }
    }

    private static (FileIdentity? Identity, uint? Links) Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (FileIdentity.Of(stream.SafeFileHandle), FileIdentity.LinksOf(stream.SafeFileHandle));
    }

    [Fact]
    public void TwoNamesOfOneFile_HaveOneIdentity_AndACopyHasItsOwn()
    {
        var original = Path.Combine(_root, "original.bin");
        File.WriteAllBytes(original, [1, 2, 3]);
        var secondName = Path.Combine(_root, "second-name.bin");
        Symlinks.RequireHardLink(secondName, original);
        var copy = Path.Combine(_root, "copy.bin");
        File.Copy(original, copy);

        var (first, firstLinks) = Read(original);
        var (second, secondLinks) = Read(secondName);
        var (copied, copiedLinks) = Read(copy);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, copied);
        Assert.Equal(2u, firstLinks);
        Assert.Equal(2u, secondLinks);
        Assert.Equal(1u, copiedLinks);
    }
}
