// SysManager · ProcessManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
            () => _service.SnapshotAsync(ct: cts.Token));
    }

    // ── window probes: one enumeration per refresh ──

    /// <summary>
    /// The session's windows are enumerated ONCE per refresh, however many processes there are.
    /// </summary>
    /// <remarks>
    /// This replaced <c>p.Responding</c> and <c>p.MainWindowHandle</c>, which each resolve the main window
    /// by walking every top-level window in the session and do not share the result. Measured on a machine
    /// with 479 processes: reading <c>Responding</c> alone cost 41.6 ms, reading <c>MainWindowHandle</c>
    /// alone 43.6 ms, and reading both 81.4 ms — additive, so the walk really did run twice per process,
    /// and 205x the cost of one pass (0.4 ms). Only 15 of those 479 processes had a window at all.
    /// <para>Counted rather than timed, because a stopwatch assertion measures how busy the machine is.
    /// One call is the property that makes the cost independent of the process count, so one call is what
    /// is asserted.</para>
    /// </remarks>
    [Fact]
    public async Task Snapshot_EnumeratesWindowsOncePerRefresh_NotOncePerProcess()
    {
        var lookups = 0;
        var service = new ProcessManagerService(
            () => { lookups++; return new Dictionary<int, IntPtr>(); },
            _ => false);

        var result = await service.SnapshotAsync();

        Assert.True(result.Count > 1, $"only {result.Count} processes were listed — this cannot distinguish "
                                      + "one lookup per refresh from one per process");
        Assert.Equal(1, lookups);
    }

    /// <summary>
    /// A process whose main window has stopped pumping messages is reported as not responding.
    /// </summary>
    [Fact]
    public async Task Snapshot_ReportsNotResponding_ForAProcessWhoseWindowIsHung()
    {
        var self = Environment.ProcessId;
        var window = new IntPtr(0x1234);
        var probed = new List<IntPtr>();

        var service = new ProcessManagerService(
            () => new Dictionary<int, IntPtr> { [self] = window },
            handle => { probed.Add(handle); return true; });

        var result = await service.SnapshotAsync();

        var mine = Assert.Single(result, p => p.Pid == self);
        Assert.Equal("Not responding", mine.Status);
        Assert.True(mine.HasMainWindow);

        // Only the one window that exists is probed — asking about a zero handle would be asking whether
        // a window that is not there has stopped responding.
        Assert.Equal([window], probed);
    }

    /// <summary>
    /// With no window, a process is responding and has none — a service is not hung for lacking a message
    /// loop, which is also what <c>Process.Responding</c> reported (it returns true on a zero handle).
    /// </summary>
    [Fact]
    public async Task Snapshot_ReportsRunning_ForEveryProcessWithoutAWindow()
    {
        var probed = 0;
        var service = new ProcessManagerService(
            () => new Dictionary<int, IntPtr>(),
            _ => { probed++; return true; });

        var result = await service.SnapshotAsync();

        Assert.All(result, p =>
        {
            Assert.Equal("Running", p.Status);
            Assert.False(p.HasMainWindow);
        });

        // The hung probe was the expensive half — with no windows there is nothing to ask about, and the
        // stubbed probe answering "hung" proves it was never consulted rather than merely agreeing.
        Assert.Equal(0, probed);
    }

    /// <summary>
    /// A window that answers is reported as running, so the hung probe's answer is what decides the column
    /// rather than the mere presence of a window.
    /// </summary>
    [Fact]
    public async Task Snapshot_ReportsRunning_ForAProcessWhoseWindowAnswers()
    {
        var self = Environment.ProcessId;
        var service = new ProcessManagerService(
            () => new Dictionary<int, IntPtr> { [self] = new(0x1234) },
            _ => false);

        var result = await service.SnapshotAsync();

        var mine = Assert.Single(result, p => p.Pid == self);
        Assert.Equal("Running", mine.Status);
        Assert.True(mine.HasMainWindow);
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
