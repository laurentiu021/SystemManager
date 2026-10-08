// SysManager · SlowdownServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The "Why is it slow?" check (#1529): what each of the five finds and when, how the findings are ranked and worded,
/// and that a part that could not be read is said to be unread rather than found healthy.
/// </summary>
/// <remarks>
/// Every source is a fake held in a field here, so nothing on this PC is read: not its processes, its drives, its
/// startup programs or its event log. The clock waits no time at all and records what it was asked to wait.
/// </remarks>
public sealed class SlowdownServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0);
    private const int OwnPid = 4242;

    private Func<IReadOnlyList<FixedDriveService.FixedDrive>> _drives = () => [Drive("C:", 500, 300)];
    private Func<string?> _systemDrive = () => "C:";
    private IReadOnlyList<ProcessEntry> _first = [Proc(4, "System", 1), Proc(100, "explorer", 2)];
    private IReadOnlyList<ProcessEntry> _second = [Proc(4, "System", 1), Proc(100, "explorer", 2)];
    private readonly List<IReadOnlySet<int>?> _asked = [];
    private Func<CancellationToken, Task<SystemSnapshot>> _vitals = _ => Task.FromResult(Vitals());
    private Func<CancellationToken, Task<StartupScan>> _startup = _ => Task.FromResult(Startup(5));
    private BootRecord? _lastStart;
    private IReadOnlyList<BootDegradation>? _blamed = [];
    private readonly InstantClock _clock = new(Now);

    private SlowdownService NewService() => new(
        (known, _) =>
        {
            _asked.Add(known);
            return Task.FromResult(_asked.Count == 1 ? _first : _second);
        },
        () => _drives(),
        () => _systemDrive(),
        ct => _vitals(ct),
        ct => _startup(ct),
        _ => Task.FromResult(_lastStart),
        _ => Task.FromResult(_blamed),
        name => ProcessDescriptionService.Instance.Lookup(name),
        OwnPid,
        _clock);

    private Task<SlowdownReport> CheckAsync() => NewService().CheckAsync();

    private static FixedDriveService.FixedDrive Drive(string letter, double sizeGB, double freeGB) =>
        new(letter, "Local Disk", "NTFS", sizeGB, freeGB, "", "");

    private static ProcessEntry Proc(int pid, string name, double cpu, string description = "") =>
        new() { Pid = pid, Name = name, CpuPercent = cpu, Description = description };

    private static SystemSnapshot Vitals(double usedPercent = 50, double totalGB = 16, double uptimeDays = 1) => new(
        new OsInfo("Windows 11", "10.0", "26100", TimeSpan.FromDays(uptimeDays), "64-bit"),
        new CpuInfo("A processor", 8, 16, 3000, 10),
        new MemoryInfo(totalGB, totalGB * (100 - usedPercent) / 100, totalGB * usedPercent / 100, usedPercent, []),
        [],
        Now);

    private static StartupScan Startup(int enabled, int disabled = 0, bool tasksListed = true) => new(
        [
            .. Enumerable.Range(1, enabled).Select(i => new StartupEntry { Name = $"Program {i}", IsEnabled = true }),
            .. Enumerable.Range(1, disabled).Select(i => new StartupEntry { Name = $"Turned off {i}", IsEnabled = false }),
        ],
        tasksListed);

    private static BootDegradation Blamed(DateTime when, string name, long ms, string kind = "Application") =>
        new(when, kind, name, ms);

    /// <summary>
    /// Records every report, from whichever thread makes it: the five finish on different threads, and a list two of them
    /// add to at once can lose one.
    /// </summary>
    private sealed class Reports : IProgress<SlowdownProbeStatus>
    {
        private readonly List<SlowdownProbeStatus> _seen = [];

        public IReadOnlyList<SlowdownProbeStatus> Seen
        {
            get
            {
                lock (_seen) return [.. _seen];
            }
        }

        public void Report(SlowdownProbeStatus value)
        {
            lock (_seen) _seen.Add(value);
        }
    }

    /// <summary>A clock at <see cref="Now"/>, in a zone with no offset, whose waits end at once and are written down.</summary>
    private sealed class InstantClock(DateTime now) : TimeProvider
    {
        private readonly List<TimeSpan> _waits = [];

        public IReadOnlyList<TimeSpan> Waits
        {
            get
            {
                lock (_waits) return [.. _waits];
            }
        }

        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_waits) _waits.Add(dueTime);

            // At once, but not inside this call: the delay keeps the timer this returns, and should find it there.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new SpentTimer();
        }

        private sealed class SpentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ── Disk space ────────────────────────────────────────────────────────

    [Fact]
    public async Task ADriveNearlyFull_LeadsTheList_InRed_WithFreeingSpaceAsTheAction()
    {
        _drives = () => [Drive("C:", 118, 4)];

        var report = await CheckAsync();

        var drive = Assert.Single(report.Findings);
        Assert.Equal(SlowdownKind.DriveNearlyFull, drive.Kind);
        Assert.Equal(1, drive.Rank);
        Assert.Equal(StatusColors.Bad, drive.ColorHex);
        Assert.Equal("Only 4 GB is left on the C: drive", drive.Title);
        Assert.Equal("That is 97% of the drive in use. Windows needs room to work, and with this little left everything "
                     + "gets slower, not just saving files. This is the one worth fixing first.", drive.Detail);
        Assert.Equal(
            [
                new SlowdownAction("Free up space", "nav-deep-cleanup", "Deep Cleanup", IsPrimary: true),
                new SlowdownAction("See what is using it", "nav-disk-analyzer", "Disk Analyzer"),
            ],
            drive.Actions);
    }

    [Fact]
    public async Task ADriveGettingFull_RanksAfterABusyProgram_InAmber_WithNoPrimaryAction()
    {
        _drives = () => [Drive("C:", 100, 18)];
        _first = [Proc(10, "chrome", 70)];
        _second = [Proc(10, "chrome", 70)];

        var report = await CheckAsync();

        Assert.Equal([SlowdownKind.ProcessorBusy, SlowdownKind.DriveFilling], report.Findings.Select(f => f.Kind));
        Assert.Equal([1, 2], report.Findings.Select(f => f.Rank));
        var drive = report.Findings[1];
        Assert.Equal(StatusColors.Warning, drive.ColorHex);
        Assert.Equal("Only 18 GB is left on the C: drive", drive.Title);
        Assert.Equal("That is 82% of the drive in use. Windows needs room to work and to install its updates, and this is "
                     + "getting tight.", drive.Detail);
        Assert.DoesNotContain(drive.Actions, a => a.IsPrimary);
    }

    /// <summary>A big drive at a high percentage with plenty of room left is not a problem, as the Health Score says too.</summary>
    [Fact]
    public async Task ALargeDrive_WithPlentyOfRoomLeft_IsNotAFinding_AtAHighPercentage()
    {
        _drives = () => [Drive("C:", 4000, 300)];

        var report = await CheckAsync();

        Assert.Empty(report.Findings);
        Assert.Empty(report.NotRead);
    }

    /// <summary>
    /// The drive is a finding exactly when the Health Score recommends freeing space, and the urgent kind exactly when that
    /// recommendation is critical — so the two cards on the Dashboard cannot disagree about the same drive.
    /// </summary>
    [Theory]
    [InlineData(500, 300)]  // plenty
    [InlineData(100, 30)]   // 30% free
    [InlineData(100, 18)]   // under 20 GB
    [InlineData(200, 24)]   // 12% free, under 25 GB
    [InlineData(200, 26)]   // 13% free, but 26 GB left
    [InlineData(200, 15)]   // 7.5% free, with 15 GB left
    [InlineData(118, 4)]    // nearly full
    [InlineData(40, 9)]     // 22% free, but under 10 GB
    [InlineData(4000, 300)] // 7.5% free of a very large drive
    public async Task TheDriveFinding_AgreesWithTheHealthScore(double sizeGB, double freeGB)
    {
        _drives = () => [Drive("C:", sizeGB, freeGB)];

        var report = await CheckAsync();
        var health = HealthScoreService.Evaluate(Vitals(), disks: null, battery: null, _drives(), "C:");

        var recommendation = health.Recommendations.SingleOrDefault(r => r.NavTargetId == "nav-deep-cleanup");
        var drive = report.Findings.SingleOrDefault(f => f.Kind is SlowdownKind.DriveNearlyFull or SlowdownKind.DriveFilling);
        Assert.Equal(recommendation is not null, drive is not null);
        if (drive is not null)
            Assert.Equal(recommendation!.Severity == "critical", drive.Kind == SlowdownKind.DriveNearlyFull);
    }

    [Fact]
    public async Task LessThanAGigabyteLeft_SaysSo()
    {
        _drives = () => [Drive("C:", 120, 0)];

        var report = await CheckAsync();

        Assert.Equal("Less than 1 GB is left on the C: drive", Assert.Single(report.Findings).Title);
    }

    [Fact]
    public async Task NoSystemDriveAmongTheDrives_IsNotRead_AndTheCardSaysSo()
    {
        _drives = () => [Drive("D:", 500, 2)];

        var report = await CheckAsync();

        Assert.Empty(report.Findings);
        Assert.Equal([SlowdownProbe.DiskSpace], report.NotRead);
        Assert.Equal("Not checked this time: disk space. The result above does not cover it.", report.NotCheckedDisplay);
        Assert.Equal("Checked at 15:00 · 4 of 5 things looked at", report.Summary);
    }

    [Fact]
    public async Task AnUnknownSystemDrive_IsNotRead()
    {
        _systemDrive = () => null;

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.DiskSpace], report.NotRead);
    }

    [Fact]
    public async Task DrivesThatCannotBeListed_AreNotRead_AndTheRestIsStillChecked()
    {
        _drives = () => throw new IOException("the device is not ready");
        _first = [Proc(10, "chrome", 70)];
        _second = [Proc(10, "chrome", 70)];

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.DiskSpace], report.NotRead);
        Assert.Equal(SlowdownKind.ProcessorBusy, Assert.Single(report.Findings).Kind);
    }

    // ── What is running ───────────────────────────────────────────────────

    [Fact]
    public async Task AProgramBusyInBothSamples_IsNamed_ByItsOwnName_AcrossItsProcesses()
    {
        _first = [Proc(10, "chrome", 30), Proc(11, "chrome", 31), Proc(100, "explorer", 5)];
        _second = [Proc(10, "chrome", 32), Proc(11, "chrome", 30), Proc(100, "explorer", 5)];

        var report = await CheckAsync();

        var busy = Assert.Single(report.Findings);
        Assert.Equal(SlowdownKind.ProcessorBusy, busy.Kind);
        Assert.Equal(StatusColors.Warning, busy.ColorHex);
        Assert.Equal("Google Chrome is using 61% of the processor", busy.Title);
        Assert.Equal("Across its 2 processes, right now. That may be exactly what you want if you are watching or playing "
                     + "something. It is only worth acting on if the PC feels slow while you are not.", busy.Detail);
        Assert.Equal([new SlowdownAction("See what is running", "nav-processes", "Process Manager")], busy.Actions);
    }

    [Fact]
    public async Task AProgramBusyInOnlyOneSample_IsNotNamed()
    {
        _first = [Proc(10, "chrome", 80)];
        _second = [Proc(10, "chrome", 10)];

        var report = await CheckAsync();

        Assert.Empty(report.Findings);
        Assert.Empty(report.NotRead);
    }

    [Fact]
    public async Task SysManagerItself_AndTheIdleProcess_AreNeverNamed()
    {
        _first = [Proc(OwnPid, "SysManager", 90), Proc(0, "Idle", 95), Proc(50, "notepad", 5)];
        _second = [Proc(OwnPid, "SysManager", 90), Proc(0, "Idle", 95), Proc(50, "notepad", 5)];

        var report = await CheckAsync();

        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task APartOfWindows_IsSaidToBeBusyWithAJobOfItsOwn_NotWhatTheUserWants()
    {
        _first = [Proc(20, "MsMpEng", 50)];
        _second = [Proc(20, "MsMpEng", 55)];

        var busy = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal("Microsoft Defender Antimalware is using 50% of the processor", busy.Title);
        Assert.Equal("Right now, and not just for a moment. It is part of Windows, and that usually means it is busy with a "
                     + "job of its own, such as a scan or an update, that finishes by itself.", busy.Detail);
    }

    [Theory]
    [InlineData("Example Sync", "Example Sync is using 45% of the processor")]
    [InlineData("", "examplesync is using 45% of the processor")]
    public async Task AProgramSysManagerHasNoRecordOf_IsNamedByItsFile_ThenByItsProcessName(string fileDescription, string title)
    {
        _first = [Proc(30, "examplesync", 45, fileDescription)];
        _second = [Proc(30, "examplesync", 45)];

        Assert.Equal(title, Assert.Single((await CheckAsync()).Findings).Title);
    }

    [Theory]
    [InlineData(40.0, true)]
    [InlineData(39.9, false)]
    public async Task ProgramsAreNamedFromTheThreshold_Up(double percent, bool named)
    {
        _first = [Proc(10, "chrome", percent)];
        _second = [Proc(10, "chrome", percent)];

        Assert.Equal(named, (await CheckAsync()).Findings.Any(f => f.Kind == SlowdownKind.ProcessorBusy));
    }

    [Fact]
    public async Task TheSecondSample_ComesASecondAfterTheFirst_AndSkipsWhatItAlreadyRead()
    {
        _first = [Proc(10, "chrome", 3), Proc(11, "explorer", 1)];

        await CheckAsync();

        Assert.Equal([SlowdownService.SampleGap], _clock.Waits);
        Assert.Equal(2, _asked.Count);
        Assert.Null(_asked[0]);
        Assert.Equal([10, 11], _asked[1]!.Order());
    }

    [Fact]
    public async Task NoProcessList_IsNotRead()
    {
        _first = [];

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.Processor], report.NotRead);
    }

    // ── Startup programs ──────────────────────────────────────────────────

    [Fact]
    public async Task MoreThanTenPrograms_IsAFinding_CountedAsStartupManagerCountsThem()
    {
        _startup = _ => Task.FromResult(Startup(enabled: 11, disabled: 4));

        var startup = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal(SlowdownKind.StartupHeavy, startup.Kind);
        Assert.Equal("11 programs start with Windows", startup.Title);
        Assert.Equal("Each one adds to how long the PC takes to be ready, and many keep running in the background "
                     + "afterwards. Turning one off is reversible: it can be switched back on from the same screen.",
                     startup.Detail);
        Assert.Equal([new SlowdownAction("Review startup programs", "nav-startup", "Startup Manager")], startup.Actions);
    }

    [Fact]
    public async Task TenPrograms_IsNotAFinding()
    {
        _startup = _ => Task.FromResult(Startup(enabled: 10, disabled: 30));

        var report = await CheckAsync();

        Assert.Empty(report.Findings);
        Assert.Empty(report.NotRead);
    }

    [Fact]
    public async Task WithoutTheScheduledTasks_TheCountIsSaidToBeAtLeast()
    {
        var progress = new Reports();
        _startup = _ => Task.FromResult(Startup(enabled: 12, tasksListed: false));

        var report = await NewService().CheckAsync(progress);

        Assert.Equal("At least 12 programs start with Windows", Assert.Single(report.Findings).Title);
        Assert.Equal("Startup programs — at least 12 start with Windows",
                     progress.Seen.Single(r => r.Probe == SlowdownProbe.Startup).Line);
    }

    [Fact]
    public async Task WhatWindowsBlamedAtItsLastTimedStart_IsNamed_LongestFirst_AndNothingFromAnOlderStart()
    {
        var timed = Now.AddDays(-2);
        _lastStart = new BootRecord(timed, 66_000, 54_000, 12_000);
        _blamed =
        [
            Blamed(timed, "steam.exe", 7_700),
            Blamed(timed, "examplesync.exe", 18_500),
            Blamed(timed, "helper.exe", 3_000),
            Blamed(Now.AddDays(-10), "olderblame.exe", 30_000),
        ];

        var startup = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal("The PC is slow to start", startup.Title);
        Assert.Equal("Windows itself blamed examplesync.exe (18.5 s) and Steam (7.7 s) for slowing the last start it timed, "
                     + "on 6 October. Turning one off is reversible: it can be switched back on from the same screen.",
                     startup.Detail);
        Assert.Equal(
            [
                new SlowdownAction("Review startup programs", "nav-startup", "Startup Manager"),
                new SlowdownAction("See what slowed the start", "nav-boot-analyzer", "Boot Analyzer"),
            ],
            startup.Actions);
    }

    [Fact]
    public async Task ADriverBlamedAboveTheRest_SendsTheUserToBootAnalyzer_NotStartupManager()
    {
        _lastStart = new BootRecord(Now.AddDays(-1), 80_000, 70_000, 10_000);
        _blamed = [Blamed(Now.AddDays(-1), "slowdriver.sys", 25_000, kind: "Driver")];

        var startup = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal("The PC is slow to start", startup.Title);
        Assert.DoesNotContain("reversible", startup.Detail, StringComparison.Ordinal);
        Assert.Equal([new SlowdownAction("See what slowed the start", "nav-boot-analyzer", "Boot Analyzer")], startup.Actions);
    }

    [Fact]
    public async Task AStartTimedMoreThanAMonthAgo_IsNotHeldAgainstThePC()
    {
        _lastStart = new BootRecord(Now.AddDays(-31), 80_000, 70_000, 10_000);
        _blamed = [Blamed(Now.AddDays(-31), "examplesync.exe", 40_000)];

        Assert.Empty((await CheckAsync()).Findings);
    }

    [Theory]
    [InlineData(20_000, false)]
    [InlineData(20_001, true)]
    public async Task BlameIsAFinding_PastTwentySeconds(long ms, bool found)
    {
        _lastStart = new BootRecord(Now.AddDays(-1), 80_000, 70_000, 10_000);
        _blamed = [Blamed(Now.AddDays(-1), "examplesync.exe", ms)];

        Assert.Equal(found, (await CheckAsync()).Findings.Any(f => f.Kind == SlowdownKind.StartupHeavy));
    }

    [Fact]
    public async Task ManyPrograms_AndBlame_AreTitledByTheCount_AndExplainedByTheBlame()
    {
        _startup = _ => Task.FromResult(Startup(enabled: 14));
        _lastStart = new BootRecord(Now.AddDays(-1), 80_000, 70_000, 10_000);
        _blamed = [Blamed(Now.AddDays(-1), "steam.exe", 4_200)];

        var startup = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal("14 programs start with Windows", startup.Title);
        Assert.StartsWith("Windows itself blamed Steam (4.2 s) for slowing the last start it timed, on 7 October.",
                          startup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABootLogThatCannotBeRead_LeavesTheCountToDecide()
    {
        _startup = _ => Task.FromResult(Startup(enabled: 11));
        _lastStart = null;
        _blamed = null;

        var report = await CheckAsync();

        Assert.Equal("11 programs start with Windows", Assert.Single(report.Findings).Title);
        Assert.Empty(report.NotRead);
    }

    [Fact]
    public async Task AStartupScanThatFails_IsNotRead()
    {
        _startup = _ => throw new UnauthorizedAccessException("denied");

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.Startup], report.NotRead);
    }

    // ── Memory ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemoryAtNinetyPercent_IsAFinding_WithTheNumbers()
    {
        _vitals = _ => Task.FromResult(Vitals(usedPercent: 90, totalGB: 16));

        var memory = Assert.Single((await CheckAsync()).Findings);

        Assert.Equal(SlowdownKind.MemoryFull, memory.Kind);
        Assert.Equal("Memory is at 90%", memory.Title);
        Assert.Equal("14.4 GB of 16.0 GB in use. Windows starts moving things to the disk above about 90%, and that is "
                     + "when switching between programs begins to stutter.", memory.Detail);
        Assert.Equal([new SlowdownAction("See what is using memory", "nav-processes", "Process Manager")], memory.Actions);
    }

    /// <summary>The number shown decides, so "90% in use" is never printed beside no finding.</summary>
    [Theory]
    [InlineData(89.5, "90% in use", true)]
    [InlineData(89.4, "89% in use", false)]
    public async Task TheMemoryNumberShown_IsTheOneThatDecides(double percent, string summary, bool found)
    {
        var progress = new Reports();
        _vitals = _ => Task.FromResult(Vitals(usedPercent: percent));

        var report = await NewService().CheckAsync(progress);

        Assert.Equal(summary, progress.Seen.Single(r => r.Probe == SlowdownProbe.Memory).Summary);
        Assert.Equal(found, report.Findings.Any(f => f.Kind == SlowdownKind.MemoryFull));
    }

    [Fact]
    public async Task MemoryThatReadsAsNothing_IsNotRead_AndTheUptimeStillIs()
    {
        _vitals = _ => Task.FromResult(Vitals(totalGB: 0, uptimeDays: 9));

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.Memory], report.NotRead);
        Assert.Equal(SlowdownKind.LongUptime, Assert.Single(report.Findings).Kind);
    }

    // ── Time since the last restart ───────────────────────────────────────

    [Fact]
    public async Task MoreThanAWeek_IsMentionedLast_InGrey_WithNoButton()
    {
        _vitals = _ => Task.FromResult(Vitals(usedPercent: 95, uptimeDays: 8.5));

        var report = await CheckAsync();

        Assert.Equal([SlowdownKind.MemoryFull, SlowdownKind.LongUptime], report.Findings.Select(f => f.Kind));
        var uptime = report.Findings[^1];
        Assert.Equal(2, uptime.Rank);
        Assert.Equal(StatusColors.Neutral, uptime.ColorHex);
        Assert.Equal("Windows has not restarted in 8 days", uptime.Title);
        Assert.Equal("Not a problem by itself, and worth knowing: a restart clears a lot of small slowdowns that no tool "
                     + "can tidy up while Windows is running. Shutting down may not count: with Fast Startup on, only "
                     + "Restart starts Windows afresh.", uptime.Detail);
        Assert.Empty(uptime.Actions);
        Assert.False(uptime.HasActions);
    }

    /// <summary>The restart is mentioned exactly when the Health Score recommends one.</summary>
    [Theory]
    [InlineData(6.9)]
    [InlineData(7.0)]
    [InlineData(7.1)]
    [InlineData(14)]
    [InlineData(45)]
    public async Task TheRestartLine_IsTheHealthScoresOwn(double days)
    {
        _vitals = _ => Task.FromResult(Vitals(uptimeDays: days));

        var report = await CheckAsync();
        var health = HealthScoreService.Evaluate(Vitals(uptimeDays: days), disks: null, battery: null,
                                                 [Drive("C:", 500, 300)], "C:");

        Assert.Equal(health.Recommendations.Any(r => r.Message.StartsWith("Restart recommended", StringComparison.Ordinal)),
                     report.Findings.Any(f => f.Kind == SlowdownKind.LongUptime));
    }

    [Fact]
    public async Task VitalsThatCannotBeRead_LeaveMemoryAndTheRestartNotChecked()
    {
        _vitals = _ => throw new COMException("RPC server unavailable");

        var report = await CheckAsync();

        Assert.Equal([SlowdownProbe.Memory, SlowdownProbe.Uptime], report.NotRead);
        Assert.Equal("Not checked this time: memory and the time since the last restart. The result above does not "
                     + "cover them.", report.NotCheckedDisplay);
    }

    // ── The report ────────────────────────────────────────────────────────

    [Fact]
    public async Task AHealthyPC_FindsNothing_AndMaySaySo()
    {
        var report = await CheckAsync();

        Assert.Empty(report.Findings);
        Assert.True(report.NothingFound);
        Assert.False(report.NothingFoundInWhatRan);
        Assert.False(report.HasUncheckedItems);
        Assert.Equal("", report.NotCheckedDisplay);
        Assert.Equal("Nothing obvious is slowing this PC down.", report.Headline);
        Assert.Equal("Checked at 15:00 · 5 things looked at", report.Summary);
        Assert.Equal(Now, report.CheckedAt);
    }

    [Fact]
    public async Task NothingFound_WithSomethingUnread_IsNotCalledNothing()
    {
        _drives = () => [];

        var report = await CheckAsync();

        Assert.False(report.NothingFound);
        Assert.True(report.NothingFoundInWhatRan);
        Assert.Equal("Nothing stood out in what could be checked.", report.Headline);
    }

    [Fact]
    public async Task OneFinding_HasNoOrderToSpeakOf_AndTwoAreWorstFirst()
    {
        _vitals = _ => Task.FromResult(Vitals(usedPercent: 95));
        var one = await CheckAsync();

        _vitals = _ => Task.FromResult(Vitals(usedPercent: 95, uptimeDays: 20));
        _asked.Clear();
        var two = await CheckAsync();

        Assert.Equal("Checked at 15:00 · 5 things looked at", one.Summary);
        Assert.Equal("One thing is worth a look.", one.Headline);
        Assert.Equal("Checked at 15:00 · 5 things looked at · worst first", two.Summary);
        Assert.Equal("2 things are worth a look, worst first.", two.Headline);
    }

    [Fact]
    public async Task EveryFinding_AtOnce_IsRankedInTheOrderTheKindsAreDeclared()
    {
        _drives = () => [Drive("C:", 118, 4)];
        _first = [Proc(10, "chrome", 70)];
        _second = [Proc(10, "chrome", 70)];
        _startup = _ => Task.FromResult(Startup(enabled: 25));
        _vitals = _ => Task.FromResult(Vitals(usedPercent: 96, uptimeDays: 30));

        var report = await CheckAsync();

        Assert.Equal(
            [SlowdownKind.DriveNearlyFull, SlowdownKind.ProcessorBusy, SlowdownKind.StartupHeavy, SlowdownKind.MemoryFull,
             SlowdownKind.LongUptime],
            report.Findings.Select(f => f.Kind));
        Assert.Equal([1, 2, 3, 4, 5], report.Findings.Select(f => f.Rank));
        Assert.Equal("5 things are worth a look, worst first.", report.Headline);
    }

    [Fact]
    public void Rank_FollowsTheDeclaredOrder_WhateverOrderTheFindingsArriveIn()
    {
        SlowdownFinding Of(SlowdownKind kind) => new(kind, kind.ToString(), "", []);
        var kinds = Enum.GetValues<SlowdownKind>();

        var ranked = SlowdownService.Rank([.. Enumerable.Reverse(kinds).Select(Of), null]);

        Assert.Equal(kinds, ranked.Select(f => f.Kind));
        Assert.Equal(Enumerable.Range(1, kinds.Length), ranked.Select(f => f.Rank));
    }

    [Fact]
    public async Task EachOfTheFive_IsReportedOnce_WhenItIsDone()
    {
        var progress = new Reports();
        _drives = () => [Drive("C:", 100, 60)];

        await NewService().CheckAsync(progress);

        Assert.Equal(Enum.GetValues<SlowdownProbe>(), progress.Seen.Select(r => r.Probe).Order());
        Assert.All(progress.Seen, r => Assert.True(r.WasRead));
        Assert.Equal("Disk space — C: 40% full", progress.Seen.Single(r => r.Probe == SlowdownProbe.DiskSpace).Line);
        Assert.Equal("What is running — nothing unusual", progress.Seen.Single(r => r.Probe == SlowdownProbe.Processor).Line);
        Assert.Equal("Time since the last restart — 1 day", progress.Seen.Single(r => r.Probe == SlowdownProbe.Uptime).Line);
    }

    [Fact]
    public async Task ACancelledCheck_EndsCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService().CheckAsync(ct: cancelled.Token));
    }

    // ── The words for each of the five ────────────────────────────────────

    [Fact]
    public void EveryProbe_HasALabelAndWordsOfItsOwn()
    {
        var probes = Enum.GetValues<SlowdownProbe>();
        var labels = SlowdownProbeStatus.Looking.Select(s => s.Label).ToList();
        var sentence = new SlowdownReport([], probes, Now).NotCheckedDisplay;

        Assert.Equal(probes, SlowdownProbeStatus.Looking.Select(s => s.Probe));
        Assert.Equal(probes.Length, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Not checked this time: disk space, what is running, startup programs, memory and the time since the "
                     + "last restart. The result above does not cover them.", sentence);
        Assert.Equal(5, SlowdownReport.ProbeCount);
    }

    [Fact]
    public void AProbesLine_SaysWhereItStands()
    {
        var looking = SlowdownProbeStatus.Looking[0];
        var read = looking with { Done = true, Read = true, Summary = "C: 40% full" };
        var unread = looking with { Done = true, Read = false };

        Assert.Equal(("Disk space…", true, false, false), (looking.Line, looking.IsLooking, looking.WasRead, looking.CouldNotRead));
        Assert.Equal(("Disk space — C: 40% full", false, true, false), (read.Line, read.IsLooking, read.WasRead, read.CouldNotRead));
        Assert.Equal(("Disk space — could not be read", false, false, true),
                     (unread.Line, unread.IsLooking, unread.WasRead, unread.CouldNotRead));
    }

    [Fact]
    public void AnActionsAccessibleName_LeadsWithTheWordsOnIt_AndSaysWhereItGoes()
        => Assert.Equal("Free up space — open Deep Cleanup",
                        new SlowdownAction("Free up space", "nav-deep-cleanup", "Deep Cleanup").AccessibleName);
}
