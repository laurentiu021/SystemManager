// SysManager · KnownFoldersTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="KnownFolders"/> — verifies the SHGetKnownFolderPath wrapper
/// still resolves real, rooted folder paths after switching to manual PWSTR
/// marshalling (the previous <c>out string</c> form leaked the CoTaskMem buffer per
/// call; the wrapper now frees it itself). Assertions stay environment-independent:
/// every Windows profile resolves these to a non-empty rooted path (either the
/// relocated Known Folder or the SpecialFolder fallback), so they don't depend on a
/// particular folder existing on a headless CI runner.
/// </summary>
public class KnownFoldersTests
{
    public static IEnumerable<object[]> Resolvers =>
    [
        [() => KnownFolders.GetDownloadsPath()],
        [() => KnownFolders.GetDocumentsPath()],
        [() => KnownFolders.GetDesktopPath()],
        [() => KnownFolders.GetPicturesPath()],
        [() => KnownFolders.GetMusicPath()],
        [() => KnownFolders.GetVideosPath()],
    ];

    [Theory]
    [MemberData(nameof(Resolvers))]
    public void GetKnownPath_ReturnsNonEmptyRootedPath(Func<string> resolve)
    {
        var path = resolve();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path), $"expected a rooted path, got: {path}");
    }

    [Fact]
    public void TryGetPath_ResolvesTheSystemFolder()
    {
        // FOLDERID_System, the folder PowerShell's notification ID starts with.
        var system = KnownFolders.TryGetPath(new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"));

        Assert.Equal(Environment.SystemDirectory, system, ignoreCase: true);
    }

    [Fact]
    public void TryGetPath_IsNull_ForAnIdThatIsNoKnownFolder()
    {
        Assert.Null(KnownFolders.TryGetPath(Guid.Empty));
        Assert.Null(KnownFolders.TryGetPath(new Guid("5D2F7C41-8A3B-4E69-B0D1-2C7E9F4A6B38")));
    }

    [Fact]
    public void GetDownloadsPath_IsStableAcrossManyCalls()
    {
        // Repeated calls must return the same path and must not crash — exercises the
        // marshal/free path many times (the leak fix runs on every call).
        var first = KnownFolders.GetDownloadsPath();
        for (var i = 0; i < 50; i++)
            Assert.Equal(first, KnownFolders.GetDownloadsPath());
    }
}
