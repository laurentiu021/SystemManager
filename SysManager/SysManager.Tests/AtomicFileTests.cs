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
    /// THE POINT OF THE HELPER. When the save fails, the previous file must survive byte-for-byte —
    /// that is the difference from <c>File.WriteAllText</c>, which would already have truncated it.
    /// <para>Driven by holding the destination open with <c>FileShare.None</c>, which is what an
    /// antivirus scanner or a backup agent does to a file it is reading: the temp is written and the
    /// swap is then refused. Deliberately NOT driven by occupying the temp path any more — the temp
    /// name is private to each call (see the ownership test below), so a test that named it would be
    /// asserting an implementation detail it is no longer entitled to know.</para>
    /// </summary>
    [Fact]
    public void WriteAllText_WhenTheSaveFails_TheOriginalFileIsUntouched()
    {
        var path = Path_("precious.json");
        const string original = "{\"history\":[\"the user's data\"]}";
        File.WriteAllText(path, original);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "replacement"));

        // The whole promise: not truncated, not empty, not partially written.
        Assert.Equal(original, File.ReadAllText(path));
        // …and the failed attempt left nothing beside it.
        Assert.Equal(["precious.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTheSaveFails_TheOriginalFileIsUntouched()
    {
        var path = Path_("precious-async.json");
        const string original = "{\"speedTests\":[1,2,3]}";
        File.WriteAllText(path, original);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(
                () => AtomicFile.WriteAllTextAsync(path, "replacement"));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(["precious-async.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    /// <summary>
    /// A save must never touch a temporary file it did not create.
    /// <para>Every write used to claim the same name, <c>&lt;path&gt;.tmp</c>, and <c>CleanUp</c> runs in
    /// a <c>finally</c> — so two writers to one destination shared one temp file and could destroy each
    /// other's work. The reachable sequence: the second writer fails to open the temp because the first
    /// still holds it, the first closes but has not yet swapped, and the second's <c>CleanUp</c> then
    /// deletes the first's finished temp. The first's swap finds nothing, BOTH writes are lost, and the
    /// destination never appears — silently, because callers log a failed save at Debug.</para>
    /// <para>Not hypothetical: <c>AboutViewModel</c>'s startup update-check records its timestamp on a
    /// background thread while the user's checkbox writes the same file from the UI thread. It surfaced
    /// as a one-in-thousands CI failure of
    /// <c>AboutViewModelTests.ConstructingTheViewModel_DoesNotTouchTheRealPreferenceFile</c>.</para>
    /// <para>Stated single-threadedly on purpose. Racing two real writers would be probabilistic, and a
    /// flaky test is a broken test; the pre-existing file below stands in for the other writer's temp
    /// and pins the invariant that makes the race impossible.</para>
    /// </summary>
    [Fact]
    public void WriteAllText_DoesNotTouchATemporaryFileItDoesNotOwn()
    {
        var path = Path_("shared.json");
        var foreignTemp = path + ".tmp";                       // the name every write used to claim
        const string foreignContents = "another writer's finished temp, not yet swapped";
        File.WriteAllText(foreignTemp, foreignContents);

        AtomicFile.WriteAllText(path, "{\"mine\":true}");

        Assert.Equal("{\"mine\":true}", File.ReadAllText(path));
        Assert.True(File.Exists(foreignTemp),
            "the save claimed a temp file it did not create — a concurrent writer's save is destroyed");
        Assert.Equal(foreignContents, File.ReadAllText(foreignTemp));
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
    [Fact]
    public void MoveIntoPlace_DestinationAppearedAfterTheProbe_StillSwapsItIn()
    {
        // Swap asks File.Exists and then acts on the answer, and those are two operations rather than one.
        // Two writers saving a file that does not exist yet — the first save of a preset, profile or
        // history — can both be told "absent", and this is the state the loser then lands in. Its
        // Move(overwrite: false) threw IOException, and callers log a failed save at Debug, so the loser's
        // data disappeared without a word. Every later write was already safe: it takes the Replace branch.
        var path = Path_("first-save.json");
        var temp = AtomicFile.UniqueTempPath(path);
        File.WriteAllText(temp, "the writer that lost the race");
        File.WriteAllText(path, "the writer that won it");

        AtomicFile.MoveIntoPlace(temp, path);

        Assert.Equal("the writer that lost the race", File.ReadAllText(path));
        Assert.False(File.Exists(temp), "the temp must not be left behind once it has been swapped in");
    }

    [Fact]
    public void MoveIntoPlace_TempIsMissing_StillFails()
    {
        // The catch filter is narrow on purpose. Only "the destination appeared" is recoverable; a missing
        // temp is a real failure and must reach the caller rather than being absorbed by the fallback.
        var path = Path_("never-written.json");
        var missing = AtomicFile.UniqueTempPath(path);

        Assert.ThrowsAny<IOException>(() => AtomicFile.MoveIntoPlace(missing, path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteAllText_FirstCreation_LeavesNoTempBehind()
    {
        // The flush opens the temp a second time, between writing it and swapping it. If that handle were
        // left open the swap would fail on a sharing violation, so this asserts the directory is clean.
        var path = Path_("clean.json");

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal(path, Assert.Single(Directory.GetFiles(_dir)));
    }

}
