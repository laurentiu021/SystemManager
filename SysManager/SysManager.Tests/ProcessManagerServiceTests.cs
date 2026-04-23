// SysManager · ProcessManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessManagerService"/>. Uses the real process
/// list (read-only tests only — no kill tests in CI).
/// </summary>
public class ProcessManagerServiceTests
{
    private readonly ProcessManagerService _service = new();

    [Fact]
    public async Task Snapshot_ReturnsNonEmpty()
    {
        var result = await _service.SnapshotAsync();
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Snapshot_ContainsCurrentProcess()
    {
        var result = await _service.SnapshotAsync();
        var current = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Contains(result, p => p.Pid == current.Id);
    }

    [Fact]
    public async Task Snapshot_EntriesHaveValidPid()
    {
        var result = await _service.SnapshotAsync();
        Assert.All(result, p => Assert.True(p.Pid >= 0));
    }

    [Fact]
    public async Task Snapshot_EntriesHaveNonEmptyName()
    {
        var result = await _service.SnapshotAsync();
        Assert.All(result, p => Assert.False(string.IsNullOrEmpty(p.Name)));
    }

    [Fact]
    public async Task Snapshot_EntriesHaveNonNegativeMemory()
    {
        var result = await _service.SnapshotAsync();
        Assert.All(result, p => Assert.True(p.MemoryBytes >= 0));
    }

    [Fact]
    public async Task Snapshot_EntriesHaveStatus()
    {
        var result = await _service.SnapshotAsync();
        Assert.All(result, p => Assert.False(string.IsNullOrEmpty(p.Status)));
    }

    [Fact]
    public async Task Snapshot_EntriesHaveThreadCount()
    {
        var result = await _service.SnapshotAsync();
        // Most processes have threads, but system/idle may report 0
        Assert.All(result, p => Assert.True(p.ThreadCount >= 0));
    }

    [Fact]
    public async Task Snapshot_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _service.SnapshotAsync(cts.Token));
    }

    // ── KillProcess ──

    [Fact]
    public void KillProcess_InvalidPid_ReturnsFalse()
    {
        var result = ProcessManagerService.KillProcess(-1);
        Assert.False(result);
    }

    [Fact]
    public void KillProcess_NonExistentPid_ReturnsFalse()
    {
        var result = ProcessManagerService.KillProcess(999999);
        Assert.False(result);
    }

    // ── OpenFileLocation ──

    [Fact]
    public void OpenFileLocation_NullPath_DoesNotThrow()
    {
        ProcessManagerService.OpenFileLocation(null!);
    }

    [Fact]
    public void OpenFileLocation_EmptyPath_DoesNotThrow()
    {
        ProcessManagerService.OpenFileLocation("");
    }

    [Fact]
    public void OpenFileLocation_NonExistentPath_DoesNotThrow()
    {
        ProcessManagerService.OpenFileLocation(@"C:\NoSuchFile_" + Guid.NewGuid().ToString("N") + ".exe");
    }

    // ── ProcessEntry model ──

    [Fact]
    public void ProcessEntry_MemoryDisplay_FormatsCorrectly()
    {
        var entry = new ProcessEntry { MemoryBytes = 52_428_800 }; // 50 MB
        Assert.Equal("50.0 MB", entry.MemoryDisplay);
    }

    [Fact]
    public void ProcessEntry_PropertyChange_Notifies()
    {
        var entry = new ProcessEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Pid = 1234;
        entry.Name = "test";
        entry.MemoryBytes = 1024;
        entry.Status = "Running";
        entry.ThreadCount = 5;

        Assert.Contains("Pid", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("MemoryBytes", changed);
        Assert.Contains("Status", changed);
        Assert.Contains("ThreadCount", changed);
    }

    [Fact]
    public void ProcessEntry_DefaultValues()
    {
        var entry = new ProcessEntry();
        Assert.Equal(0, entry.Pid);
        Assert.Equal("", entry.Name);
        Assert.Equal("", entry.Description);
        Assert.Equal(0, entry.MemoryBytes);
        Assert.Equal("", entry.Status);
        Assert.Equal("", entry.FilePath);
        Assert.Equal(0, entry.ThreadCount);
    }
}
