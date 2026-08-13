// SysManager · AtomicFileTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AtomicFile"/>, the helper that replaced 17 in-place
/// <c>File.WriteAllText</c> calls to user-data files.
/// <para>The defect being pinned: <c>File.WriteAllText</c> truncates the destination before writing,
/// so an interrupted save leaves a torn file. SysManager's loaders treat an unparseable file as "no
/// data" — several catch <c>JsonException</c> and substitute an empty list at Debug level — so a torn
/// save silently erased the user's activity history, speed-test history or volume presets instead of
/// reporting anything.</para>
/// <para>Every test writes only inside its own temp directory and deletes it in a finally, so the
/// suite never touches the real user profile.</para>
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smtest_atomic_" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void WriteAllText_CreatesTheFile_WhenItDoesNotExistYet()
    {
        var path = Path_("new.json");

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingContents()
    {
        var path = Path_("existing.json");
        File.WriteAllText(path, "old contents that must be gone");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_CreatesAMissingDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "file.json");

        AtomicFile.WriteAllText(path, "[]");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTemporaryFileBehind()
    {
        // A leftover .tmp beside the real file would be visible to the user in their config folder,
        // and on the next save File.Replace would be handed a stale source.
        var path = Path_("clean.json");

        AtomicFile.WriteAllText(path, "{}");
        AtomicFile.WriteAllText(path, "{\"second\":true}");

        Assert.Equal(["clean.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    /// <summary>
    /// THE POINT OF THE HELPER. When the write itself fails, the previous file must survive
    /// byte-for-byte — that is the difference from <c>File.WriteAllText</c>, which would already have
    /// truncated it. Driven by making the temp path un-writable (a directory cannot be overwritten by
    /// a file), which fails the write at the same moment a full disk or a crash would.
    /// </summary>
    [Fact]
    public void WriteAllText_WhenTheWriteFails_TheOriginalFileIsUntouched()
    {
        var path = Path_("precious.json");
        const string original = "{\"history\":[\"the user's data\"]}";
        File.WriteAllText(path, original);

        // Occupy the temp path with a directory so writing the temp file must fail.
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "replacement"));

        // The whole promise: not truncated, not empty, not partially written.
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTheWriteFails_TheOriginalFileIsUntouched()
    {
        var path = Path_("precious-async.json");
        const string original = "{\"speedTests\":[1,2,3]}";
        File.WriteAllText(path, original);

        Directory.CreateDirectory(path + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(
            () => AtomicFile.WriteAllTextAsync(path, "replacement"));

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesExistingContents()
    {
        var path = Path_("async.json");
        File.WriteAllText(path, "old");

        await AtomicFile.WriteAllTextAsync(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllBytes_ReplacesExistingContents()
    {
        var path = Path_("blob.bin");
        File.WriteAllBytes(path, [0xDE, 0xAD]);

        AtomicFile.WriteAllBytes(path, [0x01, 0x02, 0x03]);

        Assert.Equal([0x01, 0x02, 0x03], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteAllBytesAsync_ReplacesExistingContents()
    {
        var path = Path_("blob-async.bin");
        File.WriteAllBytes(path, [0xFF]);

        await AtomicFile.WriteAllBytesAsync(path, [0x0A, 0x0B]);

        Assert.Equal([0x0A, 0x0B], File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllText_WithAnExplicitEncoding_WritesNoByteOrderMark()
    {
        // ProfileService restores the user's config files and must not prepend a BOM, or the owning
        // service may fail to parse what it wrote.
        var path = Path_("no-bom.json");

        AtomicFile.WriteAllText(path, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteAllText_RejectsAnEmptyPath(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => AtomicFile.WriteAllText(path!, "x"));
    }

    [Fact]
    public void WriteAllText_RejectsNullContents()
    {
        Assert.Throws<ArgumentNullException>(() => AtomicFile.WriteAllText(Path_("x.json"), null!));
    }
}
