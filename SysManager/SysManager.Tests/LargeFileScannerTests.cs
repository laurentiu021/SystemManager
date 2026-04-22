using System.IO;
using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="LargeFileScanner"/>. Uses temp directories with
/// known file sizes so results are deterministic.
/// </summary>
public class LargeFileScannerTests : IDisposable
{
    private readonly string _root;
    private readonly LargeFileScanner _scanner = new();

    public LargeFileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SysManagerLFS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateFile(string name, int sizeBytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private string CreateSubDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------- basic scan ----------

    [Fact]
    public async Task Scan_EmptyDir_ReturnsEmpty()
    {
        var result = await _scanner.ScanAsync(_root, minSizeBytes: 1, top: 10);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_AllFilesBelowMin_ReturnsEmpty()
    {
        CreateFile("small.txt", 100);
        var result = await _scanner.ScanAsync(_root, minSizeBytes: 1000, top: 10);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_FindsFilesAboveMin()
    {
        CreateFile("big.bin", 2000);
        CreateFile("small.txt", 100);
        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 10);
        Assert.Single(result);
        Assert.Equal("big.bin", result[0].Name);
        Assert.Equal(2000, result[0].SizeBytes);
    }

    [Fact]
    public async Task Scan_RespectsTopLimit()
    {
        for (int i = 0; i < 10; i++)
            CreateFile($"file{i}.bin", 1000 + i * 100);

        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 3);
        Assert.Equal(3, result.Count);
        // Should be the 3 largest, sorted descending
        Assert.True(result[0].SizeBytes >= result[1].SizeBytes);
        Assert.True(result[1].SizeBytes >= result[2].SizeBytes);
    }

    [Fact]
    public async Task Scan_ResultsSortedDescending()
    {
        CreateFile("a.bin", 3000);
        CreateFile("b.bin", 1000);
        CreateFile("c.bin", 5000);
        CreateFile("d.bin", 2000);

        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 10);
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i - 1].SizeBytes >= result[i].SizeBytes,
                $"Result not sorted: {result[i - 1].SizeBytes} < {result[i].SizeBytes}");
    }

    [Fact]
    public async Task Scan_HeapEvictsSmallest()
    {
        // Create 5 files, top=3 — smallest 2 should be evicted
        CreateFile("f1.bin", 1000);
        CreateFile("f2.bin", 2000);
        CreateFile("f3.bin", 3000);
        CreateFile("f4.bin", 4000);
        CreateFile("f5.bin", 5000);

        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 3);
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, r => r.SizeBytes == 1000);
        Assert.DoesNotContain(result, r => r.SizeBytes == 2000);
    }

    // ---------- subdirectories ----------

    [Fact]
    public async Task Scan_FindsFilesInSubdirectories()
    {
        var sub = CreateSubDir("nested");
        File.WriteAllBytes(Path.Combine(sub, "deep.bin"), new byte[2000]);

        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 10);
        Assert.Single(result);
        Assert.Equal("deep.bin", result[0].Name);
    }

    // ---------- invalid inputs ----------

    [Fact]
    public async Task Scan_NullRoot_ReturnsEmpty()
    {
        var result = await _scanner.ScanAsync(null!, minSizeBytes: 1, top: 10);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_EmptyRoot_ReturnsEmpty()
    {
        var result = await _scanner.ScanAsync("", minSizeBytes: 1, top: 10);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Scan_NonExistentRoot_ReturnsEmpty()
    {
        var result = await _scanner.ScanAsync(@"C:\NoSuchDir_" + Guid.NewGuid().ToString("N"),
            minSizeBytes: 1, top: 10);
        Assert.Empty(result);
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task Scan_CancelledToken_ThrowsOrReturnsPartial()
    {
        CreateFile("big.bin", 2000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Task.Run with pre-cancelled token throws TaskCanceledException
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _scanner.ScanAsync(_root, minSizeBytes: 1, top: 10, ct: cts.Token));
    }

    // ---------- progress reporting ----------

    [Fact]
    public async Task Scan_ReportsProgress()
    {
        CreateFile("big.bin", 2000);
        var reports = new List<LargeFileScanner.LargeFileProgress>();
        var progress = new Progress<LargeFileScanner.LargeFileProgress>(p => reports.Add(p));

        await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 10, progress: progress);

        // Give Progress<T> a moment to flush (it posts to SynchronizationContext)
        await Task.Delay(100);
        // At minimum, the final "Done" report should be there
        Assert.True(reports.Count >= 1);
    }

    // ---------- LargeFileEntry model ----------

    [Fact]
    public async Task Scan_ResultEntriesHaveCorrectProperties()
    {
        var path = CreateFile("test.bin", 3000);
        var result = await _scanner.ScanAsync(_root, minSizeBytes: 500, top: 10);
        Assert.Single(result);
        var entry = result[0];
        Assert.Equal("test.bin", entry.Name);
        Assert.Equal(3000, entry.SizeBytes);
        Assert.Equal(path, entry.Path);
        Assert.True(entry.LastModified <= DateTime.Now);
        Assert.False(string.IsNullOrWhiteSpace(entry.SizeDisplay));
        Assert.False(string.IsNullOrWhiteSpace(entry.LastModifiedDisplay));
    }

    // ---------- ShouldSkip (via reflection) ----------

    [Fact]
    public void ShouldSkip_SystemPaths_ReturnsTrue()
    {
        var method = typeof(LargeFileScanner)
            .GetMethod("ShouldSkip", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object[] { @"C:\$Recycle.Bin\S-1-5-21" })!);
        Assert.True((bool)method.Invoke(null, new object[] { @"C:\System Volume Information\tracking.log" })!);
        Assert.True((bool)method.Invoke(null, new object[] { @"C:\Windows\WinSxS\amd64_something" })!);
    }

    [Fact]
    public void ShouldSkip_NormalPaths_ReturnsFalse()
    {
        var method = typeof(LargeFileScanner)
            .GetMethod("ShouldSkip", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False((bool)method.Invoke(null, new object[] { @"C:\Users\test\Documents" })!);
        Assert.False((bool)method.Invoke(null, new object[] { @"D:\Games\Steam" })!);
    }
}
