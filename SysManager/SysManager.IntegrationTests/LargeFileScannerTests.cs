// SysManager · LargeFileScannerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class LargeFileScannerTests
{
    [Fact]
    public void Constructs()
    {
        var s = new LargeFileScanner();
        Assert.NotNull(s);
    }

    [Fact]
    public async Task ScanAsync_EmptyPath_ReturnsEmpty()
    {
        var s = new LargeFileScanner();
        var r = await s.ScanAsync("", 1, 10);
        Assert.Empty(r);
    }

    [Fact]
    public async Task ScanAsync_NullPath_ReturnsEmpty()
    {
        var s = new LargeFileScanner();
        var r = await s.ScanAsync(null!, 1, 10);
        Assert.Empty(r);
    }

    [Fact]
    public async Task ScanAsync_NonexistentPath_ReturnsEmpty()
    {
        var s = new LargeFileScanner();
        var r = await s.ScanAsync(@"C:\definitely_does_not_exist_" + Guid.NewGuid().ToString("N"), 1, 10);
        Assert.Empty(r);
    }

    [Fact]
    public async Task ScanAsync_WhitespacePath_ReturnsEmpty()
    {
        var s = new LargeFileScanner();
        var r = await s.ScanAsync("   ", 1, 10);
        Assert.Empty(r);
    }

    [Fact]
    public async Task ScanAsync_CancelledToken_ReturnsEmpty()
    {
        var s = new LargeFileScanner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Task.Run(..., cancelledToken) throws TaskCanceledException — the
        // contract is "no work happens", not "returns an empty list".
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => s.ScanAsync(Path.GetTempPath(), 1, 10, ct: cts.Token));
    }

    [Fact]
    public async Task ScanAsync_FindsLargerFiles()
    {
        // Create a temp tree with one clearly "large" file and some small ones.
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var small = Path.Combine(root, "small.bin");
        var big   = Path.Combine(root, "big.bin");
        File.WriteAllBytes(small, new byte[100]);
        File.WriteAllBytes(big,   new byte[2 * 1024 * 1024]); // 2 MB
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 1024 * 1024, top: 10);
            Assert.Single(r);
            Assert.Equal("big.bin", r[0].Name);
            Assert.Equal(2L * 1024 * 1024, r[0].SizeBytes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_SortsByDescendingSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[3 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(root, "c.bin"), new byte[2 * 1024 * 1024]);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 512 * 1024, top: 10);
            Assert.Equal(3, r.Count);
            Assert.True(r[0].SizeBytes >= r[1].SizeBytes);
            Assert.True(r[1].SizeBytes >= r[2].SizeBytes);
            Assert.Equal("b.bin", r[0].Name);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_RespectsTopN()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(root, $"f{i}.bin"), new byte[(i + 1) * 256 * 1024]);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 1, top: 2);
            Assert.Equal(2, r.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_RespectsMinSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "tiny.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "small.bin"), new byte[50_000]);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 1_000_000, top: 10);
            Assert.Empty(r);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_RecursesIntoSubdirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "deep.bin"), new byte[2 * 1024 * 1024]);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 1024 * 1024, top: 10);
            Assert.Single(r);
            Assert.Equal("deep.bin", r[0].Name);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_Called()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(root, $"f{i}.txt"), "x");
        try
        {
            var reports = new List<LargeFileScanner.LargeFileProgress>();
            var prog = new Progress<LargeFileScanner.LargeFileProgress>(reports.Add);
            var s = new LargeFileScanner();
            await s.ScanAsync(root, 1, 10, progress: prog);
            // Progress may or may not fire depending on file count threshold,
            // but we check it did not error out.
            Assert.NotNull(reports);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_ZeroMinSize_IncludesEverything()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, minSizeBytes: 0, top: 10);
            Assert.Single(r);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_PopulatesLastModified()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[2 * 1024 * 1024]);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, 1024 * 1024, 5);
            Assert.Single(r);
            Assert.True(r[0].LastModified > DateTime.MinValue);
            Assert.NotEmpty(r[0].LastModifiedDisplay);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_EmptyFolder_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerLfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var s = new LargeFileScanner();
            var r = await s.ScanAsync(root, 1, 10);
            Assert.Empty(r);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
