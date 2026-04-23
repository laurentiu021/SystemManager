// SysManager · DiskAnalyzerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskAnalyzerService"/>. Uses temp directories
/// with known structure so results are deterministic.
/// </summary>
public class DiskAnalyzerServiceTests : IDisposable
{
    private readonly string _root;
    private readonly DiskAnalyzerService _service = new();

    public DiskAnalyzerServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SysManagerDA_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateFile(string relativePath, int sizeBytes)
    {
        var path = Path.Combine(_root, relativePath);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    // ── Empty / basic ──

    [Fact]
    public async Task Analyze_EmptyDir_ReturnsEmpty()
    {
        var result = await _service.AnalyzeAsync(_root);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Analyze_SingleSubfolder_ReturnsOne()
    {
        CreateFile(Path.Combine("docs", "readme.txt"), 2048);
        var result = await _service.AnalyzeAsync(_root);
        Assert.Single(result);
        Assert.Equal("docs", result[0].Name);
        Assert.Equal(2048, result[0].SizeBytes);
    }

    [Fact]
    public async Task Analyze_MultipleSubfolders_SortedBySize()
    {
        CreateFile(Path.Combine("small", "a.txt"), 1000);
        CreateFile(Path.Combine("big", "b.txt"), 5000);
        CreateFile(Path.Combine("medium", "c.txt"), 3000);

        var result = await _service.AnalyzeAsync(_root);
        Assert.Equal(3, result.Count);
        Assert.Equal("big", result[0].Name);
        Assert.Equal("medium", result[1].Name);
        Assert.Equal("small", result[2].Name);
    }

    [Fact]
    public async Task Analyze_NestedFiles_CountedInParent()
    {
        CreateFile(Path.Combine("parent", "child", "deep.bin"), 4096);
        CreateFile(Path.Combine("parent", "top.bin"), 1024);

        var result = await _service.AnalyzeAsync(_root);
        Assert.Single(result);
        Assert.Equal("parent", result[0].Name);
        Assert.Equal(4096 + 1024, result[0].SizeBytes);
        Assert.Equal(2, result[0].FileCount);
        Assert.Equal(1, result[0].FolderCount); // "child" subfolder
    }

    [Fact]
    public async Task Analyze_RootFiles_ShownSeparately()
    {
        File.WriteAllBytes(Path.Combine(_root, "rootfile.txt"), new byte[2048]);
        CreateFile(Path.Combine("sub", "nested.txt"), 1024);

        var result = await _service.AnalyzeAsync(_root);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "(files in root)");
        Assert.Contains(result, r => r.Name == "sub");
    }

    [Fact]
    public async Task Analyze_Percentages_SumTo100()
    {
        CreateFile(Path.Combine("a", "f1.bin"), 3000);
        CreateFile(Path.Combine("b", "f2.bin"), 7000);

        var result = await _service.AnalyzeAsync(_root);
        var totalPct = result.Sum(r => r.Percentage);
        Assert.InRange(totalPct, 99.0, 101.0); // rounding tolerance
    }

    [Fact]
    public async Task Analyze_Percentages_ProportionalToSize()
    {
        CreateFile(Path.Combine("big", "f.bin"), 8000);
        CreateFile(Path.Combine("small", "f.bin"), 2000);

        var result = await _service.AnalyzeAsync(_root);
        var big = result.First(r => r.Name == "big");
        var small = result.First(r => r.Name == "small");
        Assert.True(big.Percentage > small.Percentage);
    }

    // ── Invalid inputs ──

    [Fact]
    public async Task Analyze_NullRoot_ReturnsEmpty()
    {
        var result = await _service.AnalyzeAsync(null!);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Analyze_EmptyRoot_ReturnsEmpty()
    {
        var result = await _service.AnalyzeAsync("");
        Assert.Empty(result);
    }

    [Fact]
    public async Task Analyze_NonExistentRoot_ReturnsEmpty()
    {
        var result = await _service.AnalyzeAsync(@"C:\NoSuchDir_" + Guid.NewGuid().ToString("N"));
        Assert.Empty(result);
    }

    // ── Cancellation ──

    [Fact]
    public async Task Analyze_CancelledToken_Throws()
    {
        CreateFile(Path.Combine("sub", "f.bin"), 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _service.AnalyzeAsync(_root, ct: cts.Token));
    }

    // ── Progress ──

    [Fact]
    public async Task Analyze_ReportsProgress()
    {
        CreateFile(Path.Combine("a", "f.bin"), 1024);
        CreateFile(Path.Combine("b", "f.bin"), 1024);

        var reports = new List<DiskAnalyzerService.AnalysisProgress>();
        var progress = new Progress<DiskAnalyzerService.AnalysisProgress>(p => reports.Add(p));

        await _service.AnalyzeAsync(_root, progress);
        await Task.Delay(100);

        Assert.True(reports.Count >= 1);
        Assert.Contains(reports, r => r.CurrentFolder == "Done");
    }

    // ── ShouldSkip ──

    [Fact]
    public void ShouldSkip_SystemPaths_ReturnsTrue()
    {
        var method = typeof(DiskAnalyzerService)
            .GetMethod("ShouldSkip", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object[] { @"C:\$Recycle.Bin" })!);
        Assert.True((bool)method.Invoke(null, new object[] { @"C:\System Volume Information" })!);
    }

    [Fact]
    public void ShouldSkip_NormalPaths_ReturnsFalse()
    {
        var method = typeof(DiskAnalyzerService)
            .GetMethod("ShouldSkip", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False((bool)method.Invoke(null, new object[] { @"C:\Users\test" })!);
        Assert.False((bool)method.Invoke(null, new object[] { @"D:\Games" })!);
    }

    // ── Model ──

    [Fact]
    public void DiskUsageEntry_SizeDisplay_FormatsCorrectly()
    {
        var entry = new DiskUsageEntry { SizeBytes = 1536 };
        Assert.Equal("1.5 KB", entry.SizeDisplay);

        entry.SizeBytes = 1_073_741_824 + 536_870_912; // 1.5 GB
        Assert.Equal("1.5 GB", entry.SizeDisplay);
    }

    [Fact]
    public void DiskUsageEntry_PropertyChange_Notifies()
    {
        var entry = new DiskUsageEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Name = "test";
        entry.FullPath = @"C:\test";
        entry.SizeBytes = 1024;
        entry.Percentage = 50.0;
        entry.FileCount = 10;
        entry.FolderCount = 3;
        entry.IsAccessDenied = true;

        Assert.Contains("Name", changed);
        Assert.Contains("FullPath", changed);
        Assert.Contains("SizeBytes", changed);
        Assert.Contains("Percentage", changed);
        Assert.Contains("FileCount", changed);
        Assert.Contains("FolderCount", changed);
        Assert.Contains("IsAccessDenied", changed);
    }
}
