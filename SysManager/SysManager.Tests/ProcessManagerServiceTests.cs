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

    // ── Which window makes a process an app (#2869) ──

    private const int FrameHost = 4100;

    private static ProcessManagerService.TopLevelWindow Window(int handle, int pid, bool owned = false,
        uint cloaked = 0) => new(handle, pid, owned, cloaked, IsFrame: false, IntPtr.Zero, 0);

    private static ProcessManagerService.TopLevelWindow Frame(int handle, int hosted = 0, int hostedPid = 0,
        uint cloaked = 0) => new(handle, FrameHost, HasOwner: false, cloaked, IsFrame: true, hosted, hostedPid);

    [Fact]
    public async Task Snapshot_AStoreAppInItsFrame_IsAnApp_AndItsOwnWindowIsWhatTheHungCheckReads()
    {
        // Settings or Calculator on screen: ApplicationFrameHost's frame with the app's CoreWindow inside. The frame
        // was credited to ApplicationFrameHost, so "Show only apps" hid the app and a frozen one read "Running".
        // This process stands in for the app.
        var self = Environment.ProcessId;
        var probed = new List<IntPtr>();
        var service = new ProcessManagerService(
            () => ProcessManagerService.MainWindowsOf([Frame(0x10, hosted: 0x11, hostedPid: self)]),
            handle => { probed.Add(handle); return true; });

        var result = await service.SnapshotAsync();

        var app = Assert.Single(result, p => p.Pid == self);
        Assert.True(app.HasMainWindow);
        Assert.Equal("Not responding", app.Status);
        Assert.Equal([new IntPtr(0x11)], probed);
    }

    [Fact]
    public void MainWindowsOf_AFrame_CreditsTheAppInsideIt_NotTheFrameHost()
    {
        var map = ProcessManagerService.MainWindowsOf([Frame(0x10, hosted: 0x11, hostedPid: 4200)]);

        Assert.Equal(new IntPtr(0x11), Assert.Single(map, kv => kv.Key == 4200).Value);
        Assert.False(map.ContainsKey(FrameHost));
    }

    [Fact]
    public void MainWindowsOf_AFrameWithNoAppInside_MakesNothingAnApp()
    {
        // A Store app that is not on screen leaves its frame empty and keeps a CoreWindow of its own, which is what
        // credits it; the frame alone made ApplicationFrameHost, a system process, read as an app.
        Assert.Empty(ProcessManagerService.MainWindowsOf(
            [Frame(0x10), Frame(0x12, hosted: 0x13, hostedPid: FrameHost)]));
    }

    [Fact]
    public void MainWindowsOf_AWindowItsAppCloaks_OrInsideACloakedOne_MakesNothingAnApp()
    {
        var map = ProcessManagerService.MainWindowsOf(
        [
            Window(0x20, 4300, cloaked: ProcessManagerService.DwmCloakedApp),
            Window(0x21, 4301, cloaked: ProcessManagerService.DwmCloakedInherited),
            Window(0x22, 4302, cloaked: ProcessManagerService.DwmCloakedApp | ProcessManagerService.DwmCloakedShell),
            Frame(0x23, hosted: 0x24, hostedPid: 4303, cloaked: ProcessManagerService.DwmCloakedApp),
        ]);

        Assert.Empty(map);
    }

    [Fact]
    public void MainWindowsOf_AWindowTheShellCloaks_StillCounts()
    {
        // A window on another virtual desktop, or a Store app's own window while it is not on screen.
        var map = ProcessManagerService.MainWindowsOf(
            [Window(0x30, 4400, cloaked: ProcessManagerService.DwmCloakedShell)]);

        Assert.Equal(new IntPtr(0x30), Assert.Single(map).Value);
    }

    [Fact]
    public void MainWindowsOf_TakesTheTopmostUnownedWindow_AndNeverADialog()
    {
        var map = ProcessManagerService.MainWindowsOf(
            [Window(0x40, 4500, owned: true), Window(0x41, 4500), Window(0x42, 4500)]);

        Assert.Equal(new IntPtr(0x41), Assert.Single(map).Value);
    }

    // ── KillProcess ──

    [Fact]
    public void KillProcess_InvalidPid_ReportsNotRunning()
    {
        var result = ProcessManagerService.KillProcess(-1);
        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, result);
    }

    /// <summary>
    /// No process has this ID, so there is nothing to end: not running, not refused.
    /// </summary>
    /// <remarks>
    /// <c>int.MaxValue</c>, not the 999999 this used before. Windows ignores the low two bits of a process ID when it
    /// looks one up, so 999999 names the same slot as 999996 and can reach a real process.
    /// </remarks>
    [Fact]
    public void KillProcess_NonExistentPid_ReportsNotRunning_NotRefused()
    {
        var result = ProcessManagerService.KillProcess(int.MaxValue);
        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, result);
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
    public void ProcessEntry_StartTimeDisplay_FormatsTheTimestamp()
    {
        var entry = new ProcessEntry { StartTime = new DateTime(2026, 3, 9, 14, 5, 7, DateTimeKind.Local) };
        Assert.Equal("2026-03-09 14:05:07", entry.StartTimeDisplay);
    }

    /// <summary>
    /// The case the em dash exists for. <c>Process.StartTime</c> throws for most system processes without
    /// elevation and the snapshot swallows that, leaving the field at <c>default</c> — so binding the raw
    /// value would print <c>0001-01-01 00:00:00</c>, which reads as a bug rather than "not available".
    /// </summary>
    [Fact]
    public void ProcessEntry_StartTimeDisplay_WhenWindowsWouldNotSay_ShowsADashNotYearOne()
    {
        var entry = new ProcessEntry();

        Assert.Equal("—", entry.StartTimeDisplay);
        Assert.DoesNotContain("0001", entry.StartTimeDisplay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The display string has to re-raise when the timestamp arrives, because the snapshot fills
    /// <c>StartTime</c> in after construction — a computed property with no
    /// <c>NotifyPropertyChangedFor</c> would leave the column showing the em dash for the row's whole life.
    /// </summary>
    [Fact]
    public void ProcessEntry_StartTimeDisplay_NotifiesWhenTheTimestampArrives()
    {
        var entry = new ProcessEntry();
        var changed = entry.RecordPropertyChanges();

        entry.StartTime = new DateTime(2026, 3, 9, 14, 5, 7, DateTimeKind.Local);

        Assert.Contains(nameof(ProcessEntry.StartTimeDisplay), changed);
    }

    [Fact]
    public void ProcessEntry_PropertyChange_Notifies()
    {
        var entry = new ProcessEntry();
        var changed = entry.RecordPropertyChanges();

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
