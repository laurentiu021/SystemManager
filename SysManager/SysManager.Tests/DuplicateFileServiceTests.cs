// SysManager · DuplicateFileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DuplicateFileService"/>. Uses temp directories with
/// known file content so results are deterministic.
/// </summary>
public class DuplicateFileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly DuplicateFileService _service = new();

    public DuplicateFileServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SysManagerDFS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string CreateFile(string name, int sizeBytes, byte fill = 0)
    {
        var data = new byte[sizeBytes];
        Array.Fill(data, fill);
        return CreateFile(name, data);
    }

    // ── Empty / no duplicates ──

    [Fact]
    public async Task Scan_EmptyDir_ReturnsEmpty()
    {
        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_SingleFile_ReturnsEmpty()
    {
        CreateFile("only.bin", 2048);
        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_DifferentContent_ReturnsEmpty()
    {
        CreateFile("a.bin", 2048, fill: 0xAA);
        CreateFile("b.bin", 2048, fill: 0xBB);
        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_DifferentSizes_ReturnsEmpty()
    {
        CreateFile("a.bin", 2048, fill: 0xAA);
        CreateFile("b.bin", 4096, fill: 0xAA);
        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Empty(result);
    }

    // ── Duplicate detection ──

    [Fact]
    public async Task Scan_TwoIdenticalFiles_ReturnsOneGroup()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xCC);
        CreateFile("copy1.bin", content);
        CreateFile("copy2.bin", content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        Assert.Equal(2, result[0].Count);
        Assert.Equal(2, result[0].Files.Count);
    }

    [Fact]
    public async Task Scan_ThreeIdenticalFiles_ReturnsOneGroupWithThree()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xDD);
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);
        CreateFile("c.bin", content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        Assert.Equal(3, result[0].Count);
    }

    [Fact]
    public async Task Scan_TwoGroupsOfDuplicates_ReturnsTwoGroups()
    {
        var content1 = new byte[2048];
        Array.Fill(content1, (byte)0x11);
        var content2 = new byte[2048];
        Array.Fill(content2, (byte)0x22);

        CreateFile("g1a.bin", content1);
        CreateFile("g1b.bin", content1);
        CreateFile("g2a.bin", content2);
        CreateFile("g2b.bin", content2);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Scan_DuplicatesInSubdirectories_Found()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xEE);
        CreateFile("top.bin", content);
        CreateFile(Path.Combine("sub", "nested.bin"), content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        Assert.Equal(2, result[0].Files.Count);
    }

    // ── Min size filter ──

    [Fact]
    public async Task Scan_FilesBelowMinSize_Ignored()
    {
        var content = new byte[500]; // below default 1KB
        CreateFile("tiny1.bin", content);
        CreateFile("tiny2.bin", content);

        var result = (await _service.ScanAsync(_root, minSizeBytes: 1024)).Groups;
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_CustomMinSize_Respected()
    {
        var content = new byte[100];
        CreateFile("small1.bin", content);
        CreateFile("small2.bin", content);

        var result = (await _service.ScanAsync(_root, minSizeBytes: 50)).Groups;
        Assert.Single(result);
    }

    // ── Wasted bytes ──

    [Fact]
    public async Task WastedBytes_CalculatedCorrectly()
    {
        var content = new byte[4096];
        Array.Fill(content, (byte)0xFF);
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);
        CreateFile("c.bin", content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        // 3 files, 4096 each → wasted = (3-1) * 4096 = 8192
        Assert.Equal(8192, result[0].WastedBytes);
    }

    [Fact]
    public async Task Results_SortedByWastedBytesDescending()
    {
        // Group 1: 2 files × 2048 = 2048 wasted
        var small = new byte[2048];
        Array.Fill(small, (byte)0x11);
        CreateFile("s1.bin", small);
        CreateFile("s2.bin", small);

        // Group 2: 3 files × 4096 = 8192 wasted
        var big = new byte[4096];
        Array.Fill(big, (byte)0x22);
        CreateFile("b1.bin", big);
        CreateFile("b2.bin", big);
        CreateFile("b3.bin", big);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Equal(2, result.Count);
        Assert.True(result[0].WastedBytes >= result[1].WastedBytes);
    }

    // ── Invalid inputs ──

    // A folder that could not be scanned is not one with no duplicates in it: the tab said "No duplicates found" for
    // all three (#2823).

    [Fact]
    public async Task Scan_NullRoot_SaysNotFound()
    {
        var scan = await _service.ScanAsync(null!);
        Assert.Equal(DuplicateFileService.ScanFailure.NotFound, scan.Failure);
        Assert.Empty(scan.Groups);
    }

    [Fact]
    public async Task Scan_EmptyRoot_SaysNotFound()
    {
        var scan = await _service.ScanAsync("");
        Assert.Equal(DuplicateFileService.ScanFailure.NotFound, scan.Failure);
        Assert.Empty(scan.Groups);
    }

    [Fact]
    public async Task Scan_NonExistentRoot_SaysNotFound()
    {
        var scan = await _service.ScanAsync(@"C:\NoSuchDir_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(DuplicateFileService.ScanFailure.NotFound, scan.Failure);
        Assert.Empty(scan.Groups);
    }

    // ── Cancellation ──

    [Fact]
    public async Task Scan_CancelledToken_ThrowsOrReturnsPartial()
    {
        CreateFile("a.bin", 2048);
        CreateFile("b.bin", 2048);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _service.ScanAsync(_root, ct: cts.Token));
    }

    [Fact]
    public void Scan_CancelledBeforeFinalize_Throws_NotReportComplete()
    {
        // Regression: the outer scan loops BREAK on cancellation (only the mid-hash hashing
        // throws), so a scan cancelled between files used to fall through to the "Complete"
        // progress report and return partial results. It must throw instead, so the VM's cancel
        // branch shows "Scan cancelled." — mirroring LargeFileScanner's finalize. We call Scan
        // directly with a pre-cancelled token: ScanAsync's Task.Run(ct) would
        // short-circuit before the body ran, so it cannot exercise this finalize path.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ScanWith(_root, cts.Token));
    }

    // ── Progress reporting ──

    [Fact]
    public async Task Scan_ReportsProgress()
    {
        var content = new byte[2048];
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);

        // SyncProgress captures reports synchronously — no Task.Delay race.
        var progress = new SyncProgress<DuplicateFileService.ScanProgress>();

        await _service.ScanAsync(_root, progress: progress);

        Assert.True(progress.Reports.Count >= 1);
        Assert.Contains(progress.Reports, r => r.Phase == "Complete");
    }

    // ── Hash correctness ──

    [Fact]
    public async Task Scan_HashIsDeterministic()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xAB);
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        Assert.False(string.IsNullOrWhiteSpace(result[0].Hash));
        Assert.Equal(64, result[0].Hash.Length); // SHA-256 hex = 64 chars
    }

    // ── Entry properties ──

    [Fact]
    public async Task Scan_EntryPropertiesPopulated()
    {
        var content = new byte[2048];
        CreateFile("test.bin", content);
        CreateFile("test2.bin", content);

        var result = (await _service.ScanAsync(_root)).Groups;
        Assert.Single(result);
        var entry = result[0].Files[0];
        Assert.False(string.IsNullOrWhiteSpace(entry.Name));
        Assert.False(string.IsNullOrWhiteSpace(entry.Path));
        Assert.Equal(2048, entry.SizeBytes);
        Assert.True(entry.LastModified <= DateTime.Now);
    }

    // ── ShouldSkipDir (via reflection) ──

    [Fact]
    public void ShouldSkipDir_SystemPaths_ReturnsTrue()
    {
        var method = typeof(DuplicateFileService)
            .GetMethod("ShouldSkipDir", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object[] { @"C:\$Recycle.Bin\S-1-5-21" })!);
        Assert.True((bool)method.Invoke(null, new object[] { @"C:\System Volume Information" })!);
    }

    [Fact]
    public void ShouldSkipDir_NormalPaths_ReturnsFalse()
    {
        var method = typeof(DuplicateFileService)
            .GetMethod("ShouldSkipDir", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False((bool)method.Invoke(null, new object[] { @"C:\Users\test\Documents" })!);
        Assert.False((bool)method.Invoke(null, new object[] { @"D:\Games\Steam" })!);
    }

    [Fact]
    public void ShouldSkipFile_SystemFiles_ReturnsTrue()
    {
        var method = typeof(DuplicateFileService)
            .GetMethod("ShouldSkipFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object[] { "pagefile.sys" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "hiberfil.sys" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "swapfile.sys" })!);
    }

    [Fact]
    public void ShouldSkipFile_NormalFiles_ReturnsFalse()
    {
        var method = typeof(DuplicateFileService)
            .GetMethod("ShouldSkipFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False((bool)method.Invoke(null, new object[] { "document.pdf" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "photo.jpg" })!);
    }

    // ── DuplicateFileGroup model ──

    [Fact]
    public void DuplicateFileGroup_WastedBytes_Formula()
    {
        var group = new DuplicateFileGroup { FileSize = 1000, Count = 5 };
        Assert.Equal(4000, group.WastedBytes);
    }

    [Fact]
    public void DuplicateFileGroup_SingleFile_ZeroWaste()
    {
        var group = new DuplicateFileGroup { FileSize = 1000, Count = 1 };
        Assert.Equal(0, group.WastedBytes);
    }

    // ── Partial hash pre-filter (issue #80 performance) ──

    [Fact]
    public void ComputePartialHash_Exists()
    {
        // Verify the internal method exists (it's used as a pre-filter)
        var method = typeof(DuplicateFileService)
            .GetMethod("ComputePartialHash", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
    }

    [Fact]
    public async Task SameContentFiles_StillDetectedWithPartialHash()
    {
        // Two identical files should still be detected as duplicates
        // even with the partial hash optimization
        var content = new byte[8192]; // 8 KB — larger than partial hash window
        new Random(42).NextBytes(content);
        CreateFile("dup1.bin", content);
        CreateFile("dup2.bin", content);

        var results = (await _service.ScanAsync(_root, minSizeBytes: 1)).Groups;
        Assert.Single(results);
        Assert.Equal(2, results[0].Files.Count);
    }

    [Fact]
    public async Task DifferentFirstBytes_NotFullHashed()
    {
        // Files with same size but different first bytes should NOT be
        // detected as duplicates (partial hash filters them out)
        var content1 = new byte[8192];
        var content2 = new byte[8192];
        new Random(1).NextBytes(content1);
        new Random(2).NextBytes(content2);
        CreateFile("a.bin", content1);
        CreateFile("b.bin", content2);

        var results = (await _service.ScanAsync(_root, minSizeBytes: 1)).Groups;
        Assert.Empty(results);
    }

    // ---------- the folder the user picked, and links inside it (#2381) ----------

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "smdup_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanAsync_RootIsALink_SaysSo()
    {
        // The root guard. A junction as the chosen folder reported duplicates living somewhere else
        // entirely, and this tab's results are what the user deletes from.
        var baseDir = NewRoot();
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "a.txt"), new string('x', 4096));
        await File.WriteAllTextAsync(Path.Combine(outside, "b.txt"), new string('x', 4096));

        var rootLink = Path.Combine(baseDir, "rootlink");
        Symlinks.RequireDirectoryLink(rootLink, outside, () => Directory.Delete(baseDir, recursive: true));

        try
        {
            var result = await new DuplicateFileService().ScanAsync(rootLink, minSizeBytes: 1);
            Assert.Equal(DuplicateFileService.ScanFailure.IsLink, result.Failure);
            Assert.Empty(result.Groups);
        }
        finally { Symlinks.RemoveLinkThenTree(rootLink, baseDir); }
    }

    [Fact]
    public async Task ScanAsync_ALinkAndItsTarget_AreNotReportedAsDuplicates()
    {
        // A link is not a copy of what it points at, it IS what it points at — same size, same content, so
        // it hashed identically and the pair was offered as two copies to choose between. Deleting the link
        // frees nothing; deleting the target breaks the link.
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target.txt");
        await File.WriteAllTextAsync(target, new string('x', 8192));

        var link = Path.Combine(root, "link.txt");
        Symlinks.RequireFileLink(link, target, () => Directory.Delete(root, recursive: true));

        try
        {
            var groups = (await new DuplicateFileService().ScanAsync(root, minSizeBytes: 1)).Groups;
            Assert.Empty(groups);
        }
        finally { Symlinks.RemoveLinkThenTree(link, root); }
    }

    // ── Files stored online only are not read ─────────────────────────────────

    [Fact]
    public async Task Scan_LeavesAFileStoredOnlineOnlyUnread_AndCountsIt()
    {
        // Reading one makes OneDrive download it and keep it, and the scan reads every same-size candidate. Marked
        // here with the Offline attribute, the one of the three a test can set on an ordinary file.
        var content = new byte[2048];
        Array.Fill(content, (byte)0x5A);
        CreateFile("local.bin", content);
        var online = CreateFile("online.bin", content);
        File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
        var progress = new SyncProgress<DuplicateFileService.ScanProgress>();

        var result = (await _service.ScanAsync(_root, progress: progress)).Groups;

        Assert.Empty(result);   // the only copy beside it was never read
        var complete = Assert.Single(progress.Reports, r => r.Phase == DuplicateFileService.CompletePhase);
        Assert.Equal(1, complete.OnlineOnlySkipped);
    }

    [Theory]
    [InlineData(0x00001000, true)]    // FILE_ATTRIBUTE_OFFLINE
    [InlineData(0x00040000, true)]    // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData(0x00400000, true)]    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    [InlineData(0x00400020, true)]    // and the archive bit beside it, as Windows reports it
    [InlineData(0x00000020, false)]   // archive
    [InlineData(0x00000080, false)]   // normal
    [InlineData(0x00000003, false)]   // read-only, hidden
    public void OnlyTheAttributesOfAFileNotOnThisPC_MarkItOnlineOnly(int attributes, bool expected)
        => Assert.Equal(expected, DuplicateFileService.IsOnlineOnly((FileAttributes)attributes));

    // ── A folder that could not be scanned says why (#2823) ──────────────────

    private static FileInfo[] Files(string directory) => new DirectoryInfo(directory).GetFiles();

    /// <summary>The scan, with the real listings unless a test hands it one that fails.</summary>
    private static DuplicateFileService.DuplicateScan ScanWith(
        string root, CancellationToken ct = default,
        Func<string, FileInfo[]>? listFiles = null, Func<string, string[]>? listDirectories = null) =>
        DuplicateFileService.Scan(root, 1, null, ct, listFiles ?? Files, listDirectories ?? Directory.GetDirectories);

    public static TheoryData<Exception, DuplicateFileService.ScanFailure> RootListingFaults() => new()
    {
        { new UnauthorizedAccessException("Access to the path is denied."),
            DuplicateFileService.ScanFailure.Unreadable },
        { new IOException("The device is not ready."), DuplicateFileService.ScanFailure.Unreadable },
        // Removed between the existence check and the listing.
        { new DirectoryNotFoundException("Could not find a part of the path."),
            DuplicateFileService.ScanFailure.NotFound },
    };

    [Theory]
    [MemberData(nameof(RootListingFaults))]
    public void Scan_ARootThatCannotBeListed_SaysWhy(Exception fault, DuplicateFileService.ScanFailure expected)
    {
        // A real folder cannot be made unreadable on demand: .NET opens a directory with backup semantics, so an
        // elevated test run lists it whatever its permissions say. C:\System Volume Information is the real case.
        var result = ScanWith(_root, listFiles: dir => dir == _root ? throw fault : Files(dir));

        Assert.Equal(expected, result.Failure);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public void Scan_ASubfolderThatCannotBeListed_IsLeftOut_AndTheRestIsScanned()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0x3C);
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);
        var locked = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        File.WriteAllBytes(Path.Combine(locked, "c.bin"), content);

        var result = ScanWith(_root,
            listFiles: dir => dir == locked ? throw new UnauthorizedAccessException("denied") : Files(dir));

        Assert.Equal(DuplicateFileService.ScanFailure.None, result.Failure);
        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
    }

    [Fact]
    public async Task Scan_AFolderEveryScanLeavesOut_PickedAsTheRoot_SaysSo()
    {
        // It was skipped as the walk's first folder, and the empty result read as no duplicates.
        var recycleBin = Directory.CreateDirectory(Path.Combine(_root, "$Recycle.Bin")).FullName;
        var content = new byte[2048];
        File.WriteAllBytes(Path.Combine(recycleBin, "a.bin"), content);
        File.WriteAllBytes(Path.Combine(recycleBin, "b.bin"), content);

        var result = await _service.ScanAsync(recycleBin, minSizeBytes: 1);

        Assert.Equal(DuplicateFileService.ScanFailure.Excluded, result.Failure);
        Assert.Empty(result.Groups);
    }

    // ── Two names of one file are not two copies (#2823) ─────────────────────

    [Fact]
    public async Task Scan_TwoNamesOfOneFile_AreNotDuplicates()
    {
        // A hard link is a second name for the same file: deleting either frees nothing. On a PC, a 64 KB file with a
        // second name came back as a group of two with 65,536 bytes wasted.
        var original = CreateFile("original.bin", new byte[65536]);
        Symlinks.RequireHardLink(Path.Combine(_root, "second-name.bin"), original);

        var result = await _service.ScanAsync(_root, minSizeBytes: 1);

        Assert.Equal(DuplicateFileService.ScanFailure.None, result.Failure);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Scan_ACopyBesideTwoNamesOfOneFile_IsOneDuplicate_WastingOneFile()
    {
        var content = new byte[4096];
        Array.Fill(content, (byte)0x7E);
        var original = CreateFile("original.bin", content);
        Symlinks.RequireHardLink(Path.Combine(_root, "second-name.bin"), original);
        CreateFile("copy.bin", content);

        var group = Assert.Single((await _service.ScanAsync(_root, minSizeBytes: 1)).Groups);

        Assert.Equal(2, group.Count);
        Assert.Equal(4096, group.WastedBytes);
        Assert.Contains(group.Files, f => f.Name == "copy.bin");
    }
}
