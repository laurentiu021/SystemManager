// SysManager · ProcessManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
        var result = ProcessManagerService.KillProcess(-1, default);
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
        var result = ProcessManagerService.KillProcess(int.MaxValue, default);
        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, result);
    }

    // ── Which process holds a listed ID (#2514, #2719) ──
    // Windows gives a closed process's ID to the next one it starts. A list's start time tells the two apart, and where
    // the list has none, Windows would not give one with SysManager's rights, so a process it gives one for now is
    // another process. Such a row was acted on by its ID alone.

    private static readonly DateTime ListedStart = new(2026, 10, 31, 9, 0, 0);

    private static Func<DateTime> Gives(DateTime start) => () => start;

    private static DateTime WillNotSay() => throw new System.ComponentModel.Win32Exception(5);

    [Fact]
    public void StartedAsListed_AtTheListedStartTime_IsTheListedProcess() =>
        Assert.True(ProcessManagerService.StartedAsListed(ListedStart, Gives(ListedStart)));

    [Fact]
    public void StartedAsListed_AtAnotherTime_IsAnotherProcess() =>
        Assert.False(ProcessManagerService.StartedAsListed(ListedStart, Gives(ListedStart.AddSeconds(1))));

    [Fact]
    public void StartedAsListed_WithNoListedStartTime_AndOneWindowsGivesNow_IsAnotherProcess() =>
        Assert.False(ProcessManagerService.StartedAsListed(default, Gives(ListedStart)));

    [Fact]
    public void StartedAsListed_WithNoListedStartTime_AndNoneNow_IsTheListedProcess() =>
        Assert.True(ProcessManagerService.StartedAsListed(default, WillNotSay));

    [Fact]
    public void StartedAsListed_WithAListedStartTime_AndNoneNow_LeavesTheAnswerToTheCaller()
    {
        // As before: ending a process refuses then, and the other callers each say what they did.
        Assert.Throws<System.ComponentModel.Win32Exception>(() => ProcessManagerService.StartedAsListed(ListedStart, WillNotSay));
    }

    [Fact]
    public void StartedAsListed_ForAProcessThatHasExited_Throws()
    {
        Func<DateTime> exited = () => throw new InvalidOperationException("No process is associated with this object.");

        Assert.Throws<InvalidOperationException>(() => ProcessManagerService.StartedAsListed(default, exited));
        Assert.Throws<InvalidOperationException>(() => ProcessManagerService.StartedAsListed(ListedStart, exited));
    }

    [Fact]
    public async Task Snapshot_ReadsTheDetailsOfAProcessThatIsNotTheOneShownWithItsId()
    {
        // What the caller shows is a process, not an ID: one that took the ID of a shown process that exited had its
        // path and description skipped as that one's, and its row stayed bare for its whole lifetime (#2719). This
        // test process stands in for it, shown as one that started earlier, and as one of another name.
        using var self = Process.GetCurrentProcess();
        var itself = new ProcessManagerService.ProcessIdentity(self.Id, self.StartTime, self.ProcessName);
        var everyOther = AllRunning();
        Assert.True(everyOther.Remove(itself), "this process was not among the running ones, so nothing below is checked");
        var service = new ProcessManagerService();
        async Task<ProcessEntry> ListedWhileShownAs(ProcessManagerService.ProcessIdentity shown) =>
            (await service.SnapshotAsync(new HashSet<ProcessManagerService.ProcessIdentity>(everyOther) { shown }))
                .Single(p => p.Pid == self.Id);

        var asAnEarlierProcess = await ListedWhileShownAs(new(self.Id, self.StartTime.AddMinutes(-5), self.ProcessName));
        var asAnotherName = await ListedWhileShownAs(new(self.Id, self.StartTime, "another-program"));
        var asItself = await ListedWhileShownAs(new(self.Id, self.StartTime, self.ProcessName));

        Assert.NotEqual("", asAnEarlierProcess.FilePath);
        Assert.NotEqual("", asAnotherName.FilePath);
        Assert.Equal("", asItself.FilePath);   // and one that is shown still has those reads skipped
    }

    // Every process running now, as a caller that shows all of them hands them over, so a snapshot reads the details of
    // none but the one a test changes: reading them all costs seconds. From the list a snapshot reads, so each is the
    // identity the snapshot gives it.
    private static HashSet<ProcessManagerService.ProcessIdentity> AllRunning() =>
    [
        .. new ProcessManagerService().QueryProcesses().Select(p => new ProcessManagerService.ProcessIdentity(
            p.Pid, p.Created == 0 ? default : DateTime.FromFileTime(p.Created), p.Name)),
    ];

    // ── one read of the process list per refresh (#2854) ──
    // Each refresh opened every process three times, waited 100 ms between two reads of its CPU time, and built an
    // object for every thread on the PC. The system's process list gives all of it in one query.

    /// <summary>A clock that moves only when told to.</summary>
    private sealed class StepClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A process list that gives each read the next list a test laid out, then the last one again.</summary>
    private sealed class Listing(params List<ProcessManagerService.ListedProcess>[] reads)
    {
        public int Reads { get; private set; }

        public IReadOnlyList<ProcessManagerService.ListedProcess> Read() => reads[Math.Min(Reads++, reads.Length - 1)];
    }

    /// <summary>The service over <paramref name="listing"/>, whose waits move <paramref name="clock"/>.</summary>
    private static ProcessManagerService Over(Listing listing, StepClock clock, List<TimeSpan> waits) => new(
        () => new Dictionary<int, IntPtr>(), _ => false, clock, _ => "", listing.Read,
        wait =>
        {
            waits.Add(wait);
            clock.Advance(wait);
        });

    // Odd, so no real process has it: Windows gives process IDs in multiples of four, and the snapshot looks a new
    // one's file up.
    private const int App = 1201;
    private const int Other = 3401;

    private static ProcessManagerService.ListedProcess Listed(int pid, long cpu, long created = 1_000) =>
        new(pid, "app", Threads: 4, WorkingSet: 4096, Created: created, CpuTime: cpu);

    /// <summary>The CPU % of <paramref name="cpu"/> 100-ns units used over <paramref name="over"/>.</summary>
    private static double Percent(long cpu, TimeSpan over) =>
        Math.Round(cpu / (double)over.Ticks / Environment.ProcessorCount * 100, 1);

    private static ProcessEntry Entry(IReadOnlyList<ProcessEntry> snapshot, int pid) =>
        Assert.Single(snapshot, p => p.Pid == pid);

    /// <summary>The first refresh reads the list, waits out the window, and measures to a second read.</summary>
    [Fact]
    public async Task TheFirstRefresh_MeasuresTheCpuAcrossTheWindow()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 0)], [Listed(App, cpu: 300_000)]);

        var snapshot = await Over(listing, clock, waits).SnapshotAsync();

        Assert.Equal([ProcessManagerService.CpuWindow], waits);
        Assert.Equal(2, listing.Reads);
        Assert.Equal(Percent(300_000, ProcessManagerService.CpuWindow), Entry(snapshot, App).CpuPercent);
    }

    /// <summary>A refresh a second after the last measures that second, with one read and no wait.</summary>
    [Fact]
    public async Task ARefreshASecondLater_MeasuresTheSecond_WithOneReadAndNoWait()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 0)], [Listed(App, cpu: 1_000_000)], [Listed(App, cpu: 6_000_000)]);
        var service = Over(listing, clock, waits);
        await service.SnapshotAsync();

        clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = await service.SnapshotAsync();

        Assert.Equal([ProcessManagerService.CpuWindow], waits);
        Assert.Equal(3, listing.Reads);
        Assert.Equal(Percent(5_000_000, TimeSpan.FromSeconds(1)), Entry(snapshot, App).CpuPercent);
    }

    /// <summary>A refresh sooner than the window after the last waits out the rest of it.</summary>
    [Fact]
    public async Task ARefreshSoonerThanTheWindow_WaitsOutTheRest()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 0)], [Listed(App, cpu: 100_000)], [Listed(App, cpu: 150_000)]);
        var service = Over(listing, clock, waits);
        await service.SnapshotAsync();

        clock.Advance(TimeSpan.FromMilliseconds(30));
        var snapshot = await service.SnapshotAsync();

        Assert.Equal([ProcessManagerService.CpuWindow, TimeSpan.FromMilliseconds(70)], waits);
        Assert.Equal(Percent(50_000, ProcessManagerService.CpuWindow), Entry(snapshot, App).CpuPercent);
    }

    /// <summary>
    /// A read older than the limit is not measured from: the figure would be the average since whoever read last.
    /// </summary>
    /// <remarks>
    /// The service is the app's one, and the Dashboard's "Why is it slow?" reads it too. Measured from a read the tab
    /// made an hour before, its figures would be the hour's average, the mistake #2790 fixed for the vitals.
    /// </remarks>
    [Fact]
    public async Task ARefreshLongAfterTheLast_MeasuresAFreshWindow()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 0)], [Listed(App, cpu: 100_000)],
            [Listed(App, cpu: 9_000_000)], [Listed(App, cpu: 9_200_000)]);
        var service = Over(listing, clock, waits);
        await service.SnapshotAsync();

        clock.Advance(TimeSpan.FromSeconds(10));
        var snapshot = await service.SnapshotAsync();

        Assert.Equal([ProcessManagerService.CpuWindow, ProcessManagerService.CpuWindow], waits);
        Assert.Equal(4, listing.Reads);
        Assert.Equal(Percent(200_000, ProcessManagerService.CpuWindow), Entry(snapshot, App).CpuPercent);
    }

    /// <summary>
    /// A process is measured only against its own earlier reading: one started since, and one that took the ID of a
    /// process that ended, show no CPU until the next refresh.
    /// </summary>
    [Fact]
    public async Task AProcessWithNoEarlierReadingOfItsOwn_ShowsNoCpuYet()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 0)],
            [Listed(App, cpu: 900_000, created: 5_000), Listed(Other, cpu: 900_000, created: 5_000)]);

        var snapshot = await Over(listing, clock, waits).SnapshotAsync();

        Assert.Equal(0, Entry(snapshot, App).CpuPercent);     // the ID went to a process that started at another time
        Assert.Equal(0, Entry(snapshot, Other).CpuPercent);   // started since the first read
    }

    /// <summary>The idle process's CPU time is the processor's idle time, shown as no CPU, as before.</summary>
    [Fact]
    public async Task TheIdleProcess_ShowsNoCpu()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(0, cpu: 0)], [Listed(0, cpu: 900_000)]);

        var snapshot = await Over(listing, clock, waits).SnapshotAsync();

        Assert.Equal(0, Entry(snapshot, 0).CpuPercent);
    }

    /// <summary>A CPU time lower than the one before is not a negative use.</summary>
    [Fact]
    public async Task ACpuTimeLowerThanBefore_ShowsNoCpu()
    {
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        var listing = new Listing([Listed(App, cpu: 500_000)], [Listed(App, cpu: 400_000)]);

        var snapshot = await Over(listing, clock, waits).SnapshotAsync();

        Assert.Equal(0, Entry(snapshot, App).CpuPercent);
    }

    /// <summary>
    /// A process's start time, name, threads and memory are the list's, and a process the list gives no start time
    /// has none.
    /// </summary>
    [Fact]
    public async Task ARefresh_TakesTheStartTimeNameThreadsAndMemoryFromTheList()
    {
        var started = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Local);
        var clock = new StepClock();
        var waits = new List<TimeSpan>();
        ProcessManagerService.ListedProcess[] processes =
        [
            new(App, "browser", Threads: 31, WorkingSet: 250_000_000, Created: started.ToFileTime(), CpuTime: 0),
            new(Other, "agent", Threads: 2, WorkingSet: 8_000_000, Created: 0, CpuTime: 0),
        ];
        var listing = new Listing([.. processes]);

        var snapshot = await Over(listing, clock, waits).SnapshotAsync();

        var browser = Entry(snapshot, App);
        Assert.Equal(("browser", 31, 250_000_000L, started), (browser.Name, browser.ThreadCount, browser.MemoryBytes,
            browser.StartTime));
        Assert.Equal(default, Entry(snapshot, Other).StartTime);
    }

    // ── the list as Windows lays it out ──

    // Where the buffer was when the system filled it; the parser reads a name at its pointer less this.
    private static readonly nint BufferAddress = 0x1000_0000;

    /// <summary>
    /// Lays one <c>SYSTEM_PROCESS_INFORMATION</c> at <paramref name="at"/>, its name at <paramref name="nameAt"/>.
    /// </summary>
    private static void Lay(byte[] buffer, int at, uint next, int pid, string? name, int nameAt,
                            int threads = 1, long workingSet = 0, long created = 0, long user = 0, long kernel = 0)
    {
        var info = new ProcessManagerService.SystemProcessInformation
        {
            NextEntryOffset = next,
            NumberOfThreads = (uint)threads,
            CreateTime = created,
            UserTime = user,
            KernelTime = kernel,
            UniqueProcessId = pid,
            WorkingSetSize = (nuint)workingSet,
        };
        if (name is not null)
        {
            var bytes = Encoding.Unicode.GetBytes(name);
            bytes.CopyTo(buffer, nameAt);
            info.ImageName = new ProcessManagerService.UnicodeString
            {
                Length = (ushort)bytes.Length,
                MaximumLength = (ushort)bytes.Length,
                Buffer = BufferAddress + nameAt,
            };
        }
        MemoryMarshal.Write(buffer.AsSpan(at), in info);
    }

    [Fact]
    public void ReadProcesses_ReadsEveryEntry_WithItsNameAsProcessGivesIt()
    {
        var buffer = new byte[1024];
        Lay(buffer, 0, next: 256, pid: 4, "System", nameAt: 768, threads: 200, workingSet: 1_000, created: 11);
        Lay(buffer, 256, next: 256, pid: 1200, @"C:\Tools\Tool.EXE", nameAt: 800, threads: 7, workingSet: 2_000_000,
            created: 22, user: 30, kernel: 12);
        Lay(buffer, 512, next: 0, pid: 1300, "Registry", nameAt: 860);

        var processes = ProcessManagerService.ReadProcesses(buffer, BufferAddress);

        Assert.Equal(
        [
            new ProcessManagerService.ListedProcess(4, "System", 200, 1_000, 11, 0),
            new ProcessManagerService.ListedProcess(1200, "Tool", 7, 2_000_000, 22, 42),
            new ProcessManagerService.ListedProcess(1300, "Registry", 1, 0, 0, 0),
        ], processes);
    }

    [Fact]
    public void ReadProcesses_NamesTheIdleProcess_AndOneTheListGivesNoNameOrABadOne_ByItsId()
    {
        var buffer = new byte[1024];
        Lay(buffer, 0, next: 256, pid: 0, name: null, nameAt: 0);
        Lay(buffer, 256, next: 256, pid: 1300, name: null, nameAt: 0);
        Lay(buffer, 512, next: 0, pid: 1400, "far.exe", nameAt: 900);
        // Its name points past the end of the buffer, which is not followed.
        var info = MemoryMarshal.Read<ProcessManagerService.SystemProcessInformation>(buffer.AsSpan(512));
        info.ImageName.Buffer = BufferAddress + 4096;
        MemoryMarshal.Write(buffer.AsSpan(512), in info);

        var names = ProcessManagerService.ReadProcesses(buffer, BufferAddress).Select(p => p.Name);

        Assert.Equal(["Idle", "1300", "1400"], names);
    }

    [Fact]
    public void ReadProcesses_StopsAtTheEntryThatSaysItIsTheLast_AndAtTheEndOfTheBuffer()
    {
        var buffer = new byte[600];
        Lay(buffer, 0, next: 256, pid: 4, "System", nameAt: 520);
        Lay(buffer, 256, next: 512, pid: 1200, "app.exe", nameAt: 540);   // points past the end

        Assert.Equal([4, 1200], ProcessManagerService.ReadProcesses(buffer, BufferAddress).Select(p => p.Pid));
    }

    [Fact]
    public void TheProcessEntry_HasTheLayoutWindowsGivesIt()
    {
        // SYSTEM_PROCESS_INFORMATION from winternl.h and the documented fields after it: the parser reads by position,
        // so a field out of place reads another field's bytes.
        var wide = IntPtr.Size == 8;
        Assert.Equal([0, 4, 32, 40, 48, 56, wide ? 80 : 68, wide ? 144 : 104],
        [
            Offset(nameof(ProcessManagerService.SystemProcessInformation.NextEntryOffset)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.NumberOfThreads)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.CreateTime)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.UserTime)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.KernelTime)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.ImageName)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.UniqueProcessId)),
            Offset(nameof(ProcessManagerService.SystemProcessInformation.WorkingSetSize)),
        ]);
        Assert.Equal(wide ? 8 : 4, (int)Marshal.OffsetOf<ProcessManagerService.UnicodeString>(
            nameof(ProcessManagerService.UnicodeString.Buffer)));

        static int Offset(string field) =>
            (int)Marshal.OffsetOf<ProcessManagerService.SystemProcessInformation>(field);
    }

    /// <summary>
    /// The list read from Windows names every process as <see cref="Process.ProcessName"/> does, and gives the start
    /// time <see cref="Process.StartTime"/> gives, for this test process.
    /// </summary>
    [Fact]
    public void QueryProcesses_ListsThisProcess_AsProcessDoes()
    {
        using var self = Process.GetCurrentProcess();

        var mine = Assert.Single(new ProcessManagerService().QueryProcesses(), p => p.Pid == self.Id);

        Assert.Equal(self.ProcessName, mine.Name);
        Assert.Equal(self.StartTime, DateTime.FromFileTime(mine.Created));
        Assert.True(mine.Threads > 0);
        Assert.True(mine.CpuTime > 0);
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

    /// <summary>
    /// A program whose file cannot be read is listed without a description, and every other one as usual (#2718).
    /// </summary>
    /// <remarks>
    /// The read of the first file asked about throws as the real one does for a file renamed while its program runs;
    /// the rest are stand-ins. It used to fail the whole list.
    /// </remarks>
    [Fact]
    public async Task Snapshot_WithAProgramWhoseFileCannotBeRead_ListsItWithoutADescription_AndTheRestAsUsual()
    {
        string? gone = null;
        var service = new ProcessManagerService(() => new Dictionary<int, IntPtr>(), _ => false,
            readDescription: path =>
            {
                gone ??= path;
                return path == gone ? throw new System.IO.FileNotFoundException(null, path) : "described";
            });

        var listed = await service.SnapshotAsync();

        Assert.NotNull(gone);
        Assert.Contains(listed, e => e.FilePath == gone && e.Description == "");
        var others = listed.Where(e => e.FilePath.Length > 0 && e.FilePath != gone).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, e => Assert.Equal("described", e.Description));
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
