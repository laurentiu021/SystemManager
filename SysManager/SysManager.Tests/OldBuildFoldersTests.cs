// SysManager · OldBuildFoldersTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// What the start-up pass deletes of the folders older builds unpacked their native libraries into, and what it
/// leaves (#2858).
/// </summary>
/// <remarks>
/// It deletes inside a folder any program running as the user can write to, so each case it must leave alone is
/// tested on its own, with every file of the folder checked afterwards rather than the folder's existence alone. Every
/// time is set from <see cref="Now"/>, never read from the clock, so a folder's age is the one the case gives it.
/// </remarks>
public sealed class OldBuildFoldersTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-30);
    private static readonly Version Running = new(1, 136, 159, 0);

    private readonly string _base = Path.Combine(Path.GetTempPath(), "smobf_" + Guid.NewGuid().ToString("N"));

    // The one link a case makes, removed on its own before the tree: a recursive delete cannot take a junction, and
    // must not follow one.
    private string? _link;

    public OldBuildFoldersTests()
    {
        // The running build's own folder, as old as any other, so nothing but its being this build's spares it.
        Unpack(Own, Old);
    }

    private string Root => Path.Combine(_base, ".net");

    private string Own => Path.Combine(Root, "SysManager-v1.136.159", "Own1");

    public void Dispose()
    {
        if (_link is not null) Symlinks.RemoveLinkThenTree(_link, _base);
        else if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private void Junction(string link, string target)
    {
        _link = link;
        Symlinks.RequireJunction(link, target);
    }

    /// <summary>
    /// Unpacks a build into <paramref name="folder"/> as the host does, with the marker unless told otherwise, and
    /// dates all of it, <paramref name="folder"/> included, <paramref name="written"/>.
    /// </summary>
    private static string Unpack(string folder, DateTime written, bool marker = true)
    {
        Directory.CreateDirectory(Path.Combine(folder, "amd64"));
        if (marker) File.WriteAllText(Path.Combine(folder, OldBuildFolders.InUseMarker), "wpf");
        File.WriteAllText(Path.Combine(folder, "libSkiaSharp.dll"), "skia");
        File.WriteAllText(Path.Combine(folder, "amd64", "KernelTraceControl.dll"), "etw");
        Date(folder, written);
        return folder;
    }

    /// <summary>A release's folder, <c>SysManager-v&lt;version&gt;\Id1</c>, unpacked and dated whole.</summary>
    private string Release(string version, DateTime written, bool marker = true)
    {
        var folder = Path.Combine(Root, "SysManager-v" + version);
        Unpack(Path.Combine(folder, "Id1"), written, marker);
        Date(folder, written);
        return folder;
    }

    /// <summary>Dates every file and folder under <paramref name="folder"/>, and the folder itself.</summary>
    /// <remarks>Files first, then folders deepest first: creating an entry rewrites its folder's time.</remarks>
    private static void Date(string folder, DateTime written)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, written);
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            Directory.SetLastWriteTimeUtc(dir, written);
        Directory.SetLastWriteTimeUtc(folder, written);
    }

    private static string[] FilesUnder(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order()];

    private OldBuildFolders.Outcome Run(bool elevated = false, string? own = null, string? root = null) =>
        OldBuildFolders.Remove(root ?? Root, own ?? Own, Running, Now, elevated);

    private void AssertOwnIntact()
    {
        Assert.Equal(3, FilesUnder(Own).Length);
    }

    // ---------- what goes ----------

    [Fact]
    public void Remove_DeletesAnOlderReleasesFolder_AndCountsWhatItFreed()
    {
        var older = Release("1.136.150", Old);

        var outcome = Run();

        Assert.False(Directory.Exists(older));
        // "wpf", "skia" and "etw": 10 bytes.
        Assert.Equal(new OldBuildFolders.Outcome(1, 10, 0), outcome);
        AssertOwnIntact();
    }

    [Fact]
    public void Remove_DeletesABundleBesideItsOwn()
    {
        // A second build under the same exe name, as each winget upgrade of SysManager.exe leaves.
        var sibling = Unpack(Path.Combine(Root, "SysManager-v1.136.159", "Other2"), Old);

        var outcome = Run();

        Assert.False(Directory.Exists(sibling));
        Assert.Equal(1, outcome.Removed);
        AssertOwnIntact();
    }

    [Fact]
    public void Remove_UnderAnExeThatKeepsItsName_DeletesItsEarlierBundles()
    {
        var own = Unpack(Path.Combine(Root, "SysManager", "Now3"), Old);
        var earlier = Unpack(Path.Combine(Root, "SysManager", "Earlier4"), Old);

        var outcome = Run(own: own);

        Assert.False(Directory.Exists(earlier));
        Assert.Equal(3, FilesUnder(own).Length);
        Assert.Equal(1, outcome.Removed);
    }

    [Fact]
    public void Remove_DeletesAFolderWindowsEmptiedOfItsFiles()
    {
        // Disk Cleanup removes old files from TEMP and leaves their folders: nothing in one is loaded by anything.
        var shell = Path.Combine(Root, "SysManager-v1.20.65");
        Directory.CreateDirectory(Path.Combine(shell, "Id1", "amd64"));
        Date(shell, Old);

        var outcome = Run();

        Assert.False(Directory.Exists(shell));
        Assert.Equal(new OldBuildFolders.Outcome(1, 0, 0), outcome);
    }

    [Theory]
    [InlineData(7.0, true)]
    [InlineData(6.9, false)]
    public void Remove_GoesByAWeekSinceTheLastWrite(double daysAgo, bool removed)
    {
        var folder = Release("1.136.151", Now.AddDays(-daysAgo));

        var outcome = Run();

        Assert.Equal(!removed, Directory.Exists(folder));
        Assert.Equal(removed ? 1 : 0, outcome.Removed);
        Assert.Equal(removed ? 0 : 1, outcome.Kept);
    }

    // ---------- what stays ----------

    [Fact]
    public void Remove_WithOnlyItsOwnFolder_DeletesNothing()
    {
        Assert.Equal(default, Run());
        AssertOwnIntact();
    }

    [Fact]
    public void Remove_LeavesItsOwnFolderWhateverVersionItsNameSays()
    {
        // An exe whose name says an older version than it is, renamed or copied over: its own folder is named for that
        // older version, and is still its own.
        var own = Unpack(Path.Combine(Root, "SysManager-v1.136.100", "Own5"), Old);
        Date(Path.GetDirectoryName(own)!, Old);

        var outcome = Run(own: own);

        Assert.Equal(3, FilesUnder(own).Length);
        Assert.Equal(default, outcome);
    }

    [Theory]
    [InlineData("1.136.159")]
    [InlineData("1.136.160")]
    [InlineData("1.137.0")]
    [InlineData("2.0.0")]
    public void Remove_LeavesTheSameOrANewerRelease(string version)
    {
        // Under a renamed exe, so a same-version folder is not this build's own: it is still not older.
        var own = Unpack(Path.Combine(Root, "SysManager", "Now3"), Old);
        var folder = Release(version, Old);
        var before = FilesUnder(folder);

        var outcome = Run(own: own);

        Assert.Equal(before, FilesUnder(folder));
        Assert.Equal(default, outcome);
    }

    [Theory]
    [InlineData("OtherApp")]
    [InlineData("SysManagerX-v1.0.0")]
    [InlineData("SysManager-v1.2")]
    [InlineData("SysManager-v1.2.3.4")]
    [InlineData("SysManager-v1.2.3-beta")]
    public void Remove_LeavesFoldersNotNamedAsAnOlderRelease(string name)
    {
        var folder = Unpack(Path.Combine(Root, name, "Id1"), Old);

        var outcome = Run();

        Assert.Equal(3, FilesUnder(folder).Length);
        Assert.Equal(default, outcome);
    }

    [Fact]
    public void Remove_LeavesAFolderWithOneFileWrittenThisWeek()
    {
        var folder = Release("1.136.152", Old);
        var recent = Path.Combine(folder, "Id1", "libSkiaSharp.dll");
        File.SetLastWriteTimeUtc(recent, Now.AddHours(-1));

        var outcome = Run();

        Assert.Equal(3, FilesUnder(folder).Length);
        Assert.Equal(new OldBuildFolders.Outcome(0, 0, 1), outcome);
    }

    [Fact]
    public void Remove_LeavesAFolderWhoseMarkerARunningCopyHolds()
    {
        var folder = Release("1.136.153", Old);
        var marker = Path.Combine(folder, "Id1", OldBuildFolders.InUseMarker);

        OldBuildFolders.Outcome outcome;
        // Open without delete sharing, as a loaded library is held: Windows refuses its delete.
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            outcome = Run();
        }

        // Nothing else went first: the marker is tried before any other file.
        Assert.Equal(3, FilesUnder(folder).Length);
        Assert.Equal(new OldBuildFolders.Outcome(0, 0, 1), outcome);
    }

    [Fact]
    public void Remove_LeavesAFolderHoldingFilesButNoMarker()
    {
        var folder = Release("1.136.154", Old, marker: false);

        var outcome = Run();

        Assert.Equal(2, FilesUnder(folder).Length);
        Assert.Equal(new OldBuildFolders.Outcome(0, 0, 1), outcome);
    }

    [Fact]
    public void Remove_CountsAFolderItCouldNotWhollyDeleteAsKept()
    {
        var folder = Release("1.136.155", Old);
        var held = Path.Combine(folder, "Id1", "libSkiaSharp.dll");

        OldBuildFolders.Outcome outcome;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            outcome = Run();
        }

        Assert.True(File.Exists(held));
        Assert.Equal(0, outcome.Removed);
        Assert.Equal(1, outcome.Kept);
    }

    [Fact]
    public void Remove_Elevated_DeletesNothing()
    {
        var older = Release("1.136.150", Old);

        var outcome = Run(elevated: true);

        Assert.Equal(3, FilesUnder(older).Length);
        Assert.Equal(default, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Remove_WhenTheHostUnpackedNothing_DeletesNothing(string? own)
    {
        var older = Release("1.136.150", Old);

        var outcome = OldBuildFolders.Remove(Root, own, Running, Now, elevated: false);

        Assert.Equal(3, FilesUnder(older).Length);
        Assert.Equal(default, outcome);
    }

    [Fact]
    public void Remove_WhenItsOwnFolderIsNotUnderTheRoot_DeletesNothing()
    {
        // A build run from its output folder: the exe's folder is no bundle id under the root.
        var older = Release("1.136.150", Old);
        var elsewhere = Unpack(Path.Combine(_base, "out", "Release"), Old);

        var outcome = Run(own: elsewhere);

        Assert.Equal(3, FilesUnder(older).Length);
        Assert.Equal(default, outcome);
    }

    [Fact]
    public void Remove_WhenTheRootIsMissing_DeletesNothing()
    {
        var missing = Path.Combine(_base, "gone", ".net");

        Assert.Equal(default, Run(root: missing, own: Path.Combine(missing, "SysManager", "Id1")));
    }

    // ---------- links ----------

    [Fact]
    public void Remove_LeavesAReleaseFolderThatIsALink_AndWhatItLeadsTo()
    {
        var target = Unpack(Path.Combine(_base, "elsewhere", "Id1"), Old);
        Junction(Path.Combine(Root, "SysManager-v1.136.156"), Path.GetDirectoryName(target)!);

        var outcome = Run();

        Assert.Equal(3, FilesUnder(target).Length);
        Assert.Equal(default, outcome);
    }

    [Fact]
    public void Remove_LeavesAFolderHoldingALink_AndWhatItLeadsTo()
    {
        var folder = Release("1.136.157", Old);
        var outside = Unpack(Path.Combine(_base, "outside"), Old);
        Junction(Path.Combine(folder, "Id1", "link"), outside);
        Date(folder, Old);

        var outcome = Run();

        Assert.Equal(3, FilesUnder(outside).Length);
        Assert.Equal(3, Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).Count());
        Assert.Equal(new OldBuildFolders.Outcome(0, 0, 1), outcome);
    }

    [Fact]
    public void Remove_WhenTheRootIsReachedThroughALink_DeletesNothing()
    {
        // The real root under "real", reached through a junction "link" one level above it.
        var realRoot = Path.Combine(_base, "real", ".net");
        var older = Path.Combine(realRoot, "SysManager-v1.136.150");
        Unpack(Path.Combine(older, "Id1"), Old);
        Date(older, Old);
        Unpack(Path.Combine(realRoot, "SysManager-v1.136.159", "Own1"), Old);
        var link = Path.Combine(_base, "link");
        Junction(link, Path.Combine(_base, "real"));
        var linkedRoot = Path.Combine(link, ".net");

        var outcome = Run(root: linkedRoot, own: Path.Combine(linkedRoot, "SysManager-v1.136.159", "Own1"));

        Assert.Equal(3, FilesUnder(older).Length);
        Assert.Equal(default, outcome);
    }

    // ---------- the names it reads ----------

    [Fact]
    public void Candidates_NameOlderReleasesAndTheBundlesBesideItsOwn()
    {
        var older = Release("1.136.100", Old);
        var sibling = Unpack(Path.Combine(Root, "SysManager-v1.136.159", "Other2"), Old);
        Release("1.136.200", Old);
        Unpack(Path.Combine(Root, "OtherApp", "Id1"), Old);

        var found = OldBuildFolders.Candidates(Root, Own, Running);

        string[] expected = [older, sibling];
        Assert.Equal(expected, found.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveForThisBuild_OutsideASingleFileRun_DeletesNothing()
    {
        // The test host is no bundle, so the host unpacked nothing and this build has no folder to judge others by.
        Assert.Equal(default, OldBuildFolders.RemoveForThisBuild());
    }
}
