// SysManager · DuplicateFileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
        var result = await _service.ScanAsync(_root);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_SingleFile_ReturnsEmpty()
    {
        CreateFile("only.bin", 2048);
        var result = await _service.ScanAsync(_root);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_DifferentContent_ReturnsEmpty()
    {
        CreateFile("a.bin", 2048, fill: 0xAA);
        CreateFile("b.bin", 2048, fill: 0xBB);
        var result = await _service.ScanAsync(_root);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_DifferentSizes_ReturnsEmpty()
    {
        CreateFile("a.bin", 2048, fill: 0xAA);
        CreateFile("b.bin", 4096, fill: 0xAA);
        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Scan_DuplicatesInSubdirectories_Found()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xEE);
        CreateFile("top.bin", content);
        CreateFile(Path.Combine("sub", "nested.bin"), content);

        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root, minSizeBytes: 1024);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_CustomMinSize_Respected()
    {
        var content = new byte[100];
        CreateFile("small1.bin", content);
        CreateFile("small2.bin", content);

        var result = await _service.ScanAsync(_root, minSizeBytes: 50);
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

        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].WastedBytes >= result[1].WastedBytes);
    }

    // ── Invalid inputs ──

    [Fact]
    public async Task Scan_NullRoot_ReturnsEmpty()
    {
        var result = await _service.ScanAsync(null!);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_EmptyRoot_ReturnsEmpty()
    {
        var result = await _service.ScanAsync("");
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_NonExistentRoot_ReturnsEmpty()
    {
        var result = await _service.ScanAsync(@"C:\NoSuchDir_" + Guid.NewGuid().ToString("N"));
        Assert.Empty(result);
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

    // ── Progress reporting ──

    [Fact]
    public async Task Scan_ReportsProgress()
    {
        var content = new byte[2048];
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);

        var reports = new List<DuplicateFileService.ScanProgress>();
        var progress = new Progress<DuplicateFileService.ScanProgress>(p => reports.Add(p));

        await _service.ScanAsync(_root, progress: progress);
        await Task.Delay(100); // let Progress<T> flush

        Assert.True(reports.Count >= 1);
        Assert.Contains(reports, r => r.Phase == "Complete");
    }

    // ── Hash correctness ──

    [Fact]
    public async Task Scan_HashIsDeterministic()
    {
        var content = new byte[2048];
        Array.Fill(content, (byte)0xAB);
        CreateFile("a.bin", content);
        CreateFile("b.bin", content);

        var result = await _service.ScanAsync(_root);
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

        var result = await _service.ScanAsync(_root);
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
}
