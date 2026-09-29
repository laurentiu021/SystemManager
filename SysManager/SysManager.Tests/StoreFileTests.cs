// SysManager · StoreFileTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="StoreFile"/>: a missing file, a file that could not be read and a file that does not parse are three
/// different answers, because a store that writes back what it read must not treat the second as the first (#2521).
/// The file is held open with delete sharing only for as long as a read must fail.
/// </summary>
public sealed class StoreFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerStoreFileTests", Guid.NewGuid().ToString("N"));

    public StoreFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private string File1 => Path.Combine(_dir, "store.json");

    [Fact]
    public void ReadText_NoFileYet_IsEmpty_NotNull()
        => Assert.Equal("", StoreFile.ReadText(File1));

    [Fact]
    public void ReadText_AFileThatCanBeRead_IsItsText()
    {
        File.WriteAllText(File1, "[1,2,3]");

        Assert.Equal("[1,2,3]", StoreFile.ReadText(File1));
    }

    [Fact]
    public void ReadText_AFileThatCannotBeRead_IsNull()
    {
        File.WriteAllText(File1, "[1,2,3]");

        using (new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.Null(StoreFile.ReadText(File1));
    }

    [Fact]
    public async Task ReadTextAsync_NoFileYet_IsEmpty_NotNull()
        => Assert.Equal("", await StoreFile.ReadTextAsync(File1));

    [Fact]
    public async Task ReadTextAsync_AFileThatCanBeRead_IsItsText()
    {
        File.WriteAllText(File1, "[1,2,3]");

        Assert.Equal("[1,2,3]", await StoreFile.ReadTextAsync(File1));
    }

    [Fact]
    public async Task ReadTextAsync_AFileThatCannotBeRead_IsNull()
    {
        File.WriteAllText(File1, "[1,2,3]");

        using (new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.Null(await StoreFile.ReadTextAsync(File1));
    }

    [Fact]
    public async Task ReadTextAsync_ACancelledRead_Throws_RatherThanReadingAsUnreadable()
    {
        // "Could not be read" makes a store refuse its write. A read the caller cancelled is not that: it is the
        // caller stopping, and must reach the caller as a cancellation.
        File.WriteAllText(File1, "[1,2,3]");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StoreFile.ReadTextAsync(File1, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void SetAside_KeepsTheBytes_AndFreesTheName()
    {
        File.WriteAllText(File1, "{ not valid json");

        Assert.True(StoreFile.SetAside(File1));

        Assert.False(File.Exists(File1));
        Assert.Equal("{ not valid json", File.ReadAllText(File1 + ".unreadable"));
    }

    [Fact]
    public void SetAside_NeverReplacesAnEarlierOne()
    {
        File.WriteAllText(File1, "first");
        Assert.True(StoreFile.SetAside(File1));
        File.WriteAllText(File1, "second");

        Assert.True(StoreFile.SetAside(File1));

        Assert.Equal("first", File.ReadAllText(File1 + ".unreadable"));
        Assert.Equal("second", File.ReadAllText(File1 + ".unreadable-2"));
    }

    [Fact]
    public void SetAside_AFileThatCannotBeMoved_SaysSo_AndLeavesIt()
    {
        File.WriteAllText(File1, "{ not valid json");

        using (new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(StoreFile.SetAside(File1));

        Assert.Equal("{ not valid json", File.ReadAllText(File1));
        Assert.False(File.Exists(File1 + ".unreadable"));
    }
}
