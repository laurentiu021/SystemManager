// SysManager · TuneUpServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="TuneUpService"/> — validates orchestration logic,
/// progress reporting, and result aggregation. Actual system calls (WMI, file I/O)
/// are integration-level; these tests verify the service wires steps correctly.
/// </summary>
public class TuneUpServiceTests
{
    private static TuneUpService CreateService()
        => new(new ShortcutCleanerService(), new DiskHealthService(), new SystemInfoService(),
            NSubstitute.Substitute.For<IRecycleBin>());

    // ---------- construction ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = CreateService();
        Assert.NotNull(svc);
    }

    // ---------- TuneUpResult model ----------

    [Fact]
    public void TuneUpResult_FreedDisplay_FormatsCorrectly()
    {
        var result = new TuneUpResult { TempBytesFreed = 1024 * 1024 * 50 }; // 50 MB
        Assert.Contains("MB", result.FreedDisplay);
    }

    [Fact]
    public void TuneUpResult_FreedDisplay_ZeroBytes()
    {
        var result = new TuneUpResult { TempBytesFreed = 0 };
        Assert.Equal("0 B", result.FreedDisplay);
    }

    // The limits are HealthLimits', where the Health Score's bands end too, so the two cards give the same advice
    // (#2877), and HealthLimitsTests holds them to each other. These pin the edges: the advice starts past a limit,
    // not at it.

    [Fact]
    public void TuneUpResult_UptimeWarning_FalseAtTheRestartLimit()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(HealthLimits.RestartAdvisedAfterDays) };
        Assert.False(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_UptimeWarning_TruePastTheRestartLimit()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(HealthLimits.RestartAdvisedAfterDays + 0.01) };
        Assert.True(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_UptimeWarning_TrueAfterAMonth()
    {
        var result = new TuneUpResult { Uptime = TimeSpan.FromDays(30) };
        Assert.True(result.UptimeWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_FalseAtTheMemoryLimit()
    {
        var result = new TuneUpResult { RamUsedPercent = HealthLimits.CloseAppsAdvisedAbovePercent };
        Assert.False(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_TruePastTheMemoryLimit()
    {
        var result = new TuneUpResult { RamUsedPercent = HealthLimits.CloseAppsAdvisedAbovePercent + 0.5 };
        Assert.True(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_RamWarning_TrueAt95()
    {
        var result = new TuneUpResult { RamUsedPercent = 95 };
        Assert.True(result.RamWarning);
    }

    [Fact]
    public void TuneUpResult_WarningCount_ZeroWhenAllGood()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Healthy", ColorHex = StatusColors.Good }
            }
        };
        Assert.Equal(0, result.WarningCount);
    }

    // ── The verdict strings DiskHealthService actually produces ──────────────────────────────────
    //
    // The test above passed against the old `d.Verdict != "Healthy"` check only because its fixture
    // hand-wrote a bare "Healthy" — a value the service never emits. ApplyVerdict writes
    // "Healthy — 38 °C · wear 2% · 4210 h on" when any SMART counter is readable, and "Healthy."
    // (with a period) when none is. So every healthy disk on a real machine counted as a warning
    // while this test said otherwise: the fixture was the bug's alibi. These use the real shapes,
    // copied from DiskHealthService.ApplyVerdict, so the fixture can no longer disagree with
    // production.

    [Theory]
    [InlineData("Healthy — 38 °C · wear 2% · 4210 h on")]   // SMART counters readable
    [InlineData("Healthy.")]                                 // no counters available
    public void TuneUpResult_WarningCount_HealthyDiskWithRealVerdictText_IsNotAWarning(string verdict)
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = verdict, ColorHex = StatusColors.Good }
            }
        };

        Assert.Equal(0, result.WarningCount);
        Assert.Equal("All good", result.OverallVerdict);
        Assert.Equal(StatusColors.Good, result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_TwoHealthyDisks_DoNotReportTwoRecommendations()
    {
        // The exact user-visible symptom: an ordinary PC with two healthy drives and nothing else
        // wrong reported "2 recommendations" in amber — and then listed two disks whose own text said
        // "Healthy". The headline contradicted the detail directly beneath it.
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Healthy — 38 °C · wear 2% · 4210 h on", ColorHex = StatusColors.Good },
                new() { Name = "Disk1", Verdict = "Healthy.", ColorHex = StatusColors.Good },
            }
        };

        Assert.Equal(0, result.WarningCount);
        Assert.Equal("All good", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpService_CopiesTheVerdictSentence_NotTheRawHealthStatusEnum()
    {
        // The earlier fix made WarningCount read ColorHex instead of matching verdict TEXT, so the
        // amber-vs-green decision became correct. But TuneUpService was still copying
        // DiskHealthReport.HealthStatus — the raw WMI enum word MapHealth produces — into
        // DiskHealthSummary.Verdict, which is the field the Dashboard RENDERS. So a genuinely degraded
        // disk produced an amber "1 recommendation" headline sitting directly above a row that read
        // plainly "Healthy": the same card contradicting itself, in the opposite direction.
        //
        // Asserted at source level deliberately. The runtime value depends on this machine's actual
        // drives, so a behavioural test would be non-deterministic across machines and would pass
        // vacuously on a box whose disks report no SMART counters. The property NAME is the contract.
        var source = File.ReadAllText(Path.Combine(TestPaths.AppProject(), "Services", "TuneUpService.cs"));

        Assert.Contains("Verdict = r.Verdict", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdict = r.HealthStatus", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Drive is failing — back up now and replace it.", StatusColors.Bad)]
    [InlineData("SSD 87% worn out — plan a replacement.", StatusColors.Warning)]
    [InlineData("Running hot (61 °C). Check cooling / airflow.", StatusColors.Warning)]
    [InlineData("12 I/O errors logged. Monitor closely.", StatusColors.Warning)]
    public void TuneUpResult_WarningCount_RealProblemVerdicts_StillCount(string verdict, string colour)
    {
        // The other half: keying on the colour must not stop counting genuine problems. These are the
        // real strings from every non-healthy branch of ApplyVerdict.
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = verdict, ColorHex = colour }
            }
        };

        Assert.Equal(1, result.WarningCount);
        Assert.Equal("1 recommendation", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsBrokenShortcuts()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 5,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsUptime()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsRam()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 90,
            DiskResults = []
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_CountsUnhealthyDisk()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Warning", ColorHex = StatusColors.Warning }
            }
        };
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_WarningCount_MultipleWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 3,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 92,
            DiskResults = new List<DiskHealthSummary>
            {
                new() { Name = "Disk0", Verdict = "Warning", ColorHex = StatusColors.Warning },
                new() { Name = "Disk1", Verdict = "Healthy", ColorHex = StatusColors.Good }
            }
        };
        // shortcuts(1) + uptime(1) + ram(1) + disk0(1) = 4
        Assert.Equal(4, result.WarningCount);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_AllGood()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("All good", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_OneRecommendation()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("1 recommendation", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallVerdict_MultipleRecommendations()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal("2 recommendations", result.OverallVerdict);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_GreenWhenNoWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 0,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(StatusColors.Good, result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_OrangeFor1Or2Warnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(1),
            RamUsedPercent = 50,
            DiskResults = []
        };
        Assert.Equal(StatusColors.Warning, result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_OverallColorHex_RedFor3PlusWarnings()
    {
        var result = new TuneUpResult
        {
            BrokenShortcutsFound = 2,
            Uptime = TimeSpan.FromDays(20),
            RamUsedPercent = 92,
            DiskResults = []
        };
        Assert.Equal(StatusColors.Bad, result.OverallColorHex);
    }

    [Fact]
    public void TuneUpResult_RecycleBinSkipped_DefaultFalse()
    {
        var result = new TuneUpResult();
        Assert.False(result.RecycleBinSkipped);
    }

    [Fact]
    public void TuneUpResult_RecycleBinEmptied_DefaultFalse()
    {
        var result = new TuneUpResult();
        Assert.False(result.RecycleBinEmptied);
    }

    [Fact]
    public void DiskHealthSummary_Properties_SetCorrectly()
    {
        var summary = new DiskHealthSummary
        {
            Name = "Samsung 980 Pro",
            Verdict = "Healthy",
            ColorHex = StatusColors.Good
        };
        Assert.Equal("Samsung 980 Pro", summary.Name);
        Assert.Equal("Healthy", summary.Verdict);
        Assert.Equal(StatusColors.Good, summary.ColorHex);
    }

    // ---------- a check that could not run (#2501) ----------
    //
    // Each failed check used to leave its field at the value that means "nothing wrong", so with nothing checked
    // the card said "All good" and that memory and disks looked fine.

    [Fact]
    public void ACheckThatDidNotRun_IsNotAllGood_AndIsNamed()
    {
        var result = new TuneUpResult { NotChecked = ["the disks"] };

        Assert.Equal(0, result.WarningCount);   // not a recommendation
        Assert.False(result.AllChecksPassed);
        Assert.True(result.HasUncheckedItems);
        Assert.Equal("Some checks did not run", result.OverallVerdict);
        Assert.Equal(StatusColors.Warning, result.OverallColorHex);
        Assert.Equal("Not checked this time: the disks. The result above does not cover it.", result.NotCheckedDisplay);
    }

    [Fact]
    public void SeveralChecksThatDidNotRun_AreNamedInOneSentence()
    {
        var result = new TuneUpResult { NotChecked = ["shortcuts", "the disks", "memory", "uptime"] };

        Assert.Equal("Not checked this time: shortcuts, the disks, memory and uptime. The result above does not cover them.",
            result.NotCheckedDisplay);
    }

    [Fact]
    public void AFindingAlongsideACheckThatDidNotRun_StillLeadsTheHeadline()
    {
        var result = new TuneUpResult { BrokenShortcutsFound = 2, NotChecked = ["memory", "uptime"] };

        Assert.Equal("1 recommendation", result.OverallVerdict);
        Assert.False(result.AllChecksPassed);
    }

    [Fact]
    public void EveryCheckRan_AndFoundNothing_IsTheOnlyAllGood()
    {
        var result = new TuneUpResult();

        Assert.True(result.AllChecksPassed);
        Assert.False(result.HasUncheckedItems);
        Assert.Equal("", result.NotCheckedDisplay);
        Assert.Equal("All good", result.OverallVerdict);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public async Task AShortcutScanThatFails_IsNotChecked_NotZeroBroken(string failure)
    {
        Exception thrown = failure == "io"
            ? new IOException("The device is not ready.")
            : new UnauthorizedAccessException("Access to the path is denied.");

        Assert.Null(await TuneUpService.CountBrokenShortcutsAsync(() => throw thrown));
    }

    [Fact]
    public async Task AShortcutScanThatRuns_CountsTheBrokenOnes()
    {
        var report = new ShortcutScanReport
        {
            Broken = [new BrokenShortcut { Name = "Old app", ShortcutPath = "a.lnk", TargetPath = "gone.exe", Location = "Desktop" }],
        };

        Assert.Equal(1, await TuneUpService.CountBrokenShortcutsAsync(() => Task.FromResult(report)));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wmi")]
    [InlineData("com")]
    [InlineData("invalid")]
    public async Task ADiskReadThatFindsNothingOrFails_IsNotChecked(string failure)
    {
        Func<Task<IReadOnlyList<DiskHealthReport>>> collect = failure switch
        {
            "empty" => () => Task.FromResult<IReadOnlyList<DiskHealthReport>>([]),
            "wmi" => () => throw new System.Management.ManagementException("Invalid namespace"),
            "com" => () => throw new System.Runtime.InteropServices.COMException("The RPC server is unavailable."),
            _ => () => throw new InvalidOperationException("The object has no path."),
        };

        Assert.Null(await TuneUpService.ReadDisksAsync(collect));
    }

    [Fact]
    public async Task ADiskReadThatWorks_GivesOneRowPerDisk()
    {
        var disk = new DiskHealthReport
        {
            FriendlyName = "Samsung 980 Pro",
            Verdict = "Healthy — 38 °C · wear 2% · 4210 h on",
            VerdictColorHex = StatusColors.Good,
        };

        var rows = await TuneUpService.ReadDisksAsync(() => Task.FromResult<IReadOnlyList<DiskHealthReport>>([disk]));

        var row = Assert.Single(rows!);
        Assert.Equal("Samsung 980 Pro", row.Name);
        Assert.Equal("Healthy — 38 °C · wear 2% · 4210 h on", row.Verdict);
        Assert.Equal(StatusColors.Good, row.ColorHex);
    }

    [Theory]
    [InlineData("wmi")]
    [InlineData("com")]
    [InlineData("invalid")]
    public async Task AVitalsReadThatFails_IsNotChecked_NotZeroMemoryAndUptime(string failure)
    {
        Func<Task<SystemSnapshot>> capture = failure switch
        {
            "wmi" => () => throw new System.Management.ManagementException("Generic failure"),
            "com" => () => throw new System.Runtime.InteropServices.COMException("The RPC server is unavailable."),
            _ => () => throw new InvalidOperationException("No data."),
        };

        Assert.Null(await TuneUpService.CaptureVitalsAsync(capture));
    }

    [Fact]
    public void NotChecked_NamesEachCheckThatDidNotRun_InTheOrderTheCardReads()
    {
        Assert.Equal(["shortcuts", "the disks", "memory", "uptime"], TuneUpService.NotChecked(null, null, null));
        Assert.Empty(TuneUpService.NotChecked(0, [], new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "26200", TimeSpan.FromDays(1), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(16, 8, 8, 50, []),
            [],
            DateTime.Now)));
    }

    // ── Step 2: the Recycle Bin, and what the result says about it ─────────────

    [Fact]
    public async Task TheBin_IsLeftAlone_WhenTheUserDidNotAgree()
    {
        var asked = 0;

        var result = await TuneUpService.EmptyRecycleBinAsync(false, () =>
        {
            asked++;
            return default;
        }, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task TheBin_IsEmptied_AndWhatItHeldIsKept()
    {
        var emptied = new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(3L << 30, 12));

        Assert.Equal(emptied, await TuneUpService.EmptyRecycleBinAsync(true, () => emptied, CancellationToken.None));
    }

    private static TuneUpResult Ran(bool emptied, RecycleBinContents? before) => new()
    {
        TempBytesFreed = 120L * 1024 * 1024,
        TempFilesDeleted = 300,
        RecycleBinEmptied = emptied,
        RecycleBinBefore = before,
    };

    [Fact]
    public void AnEmptiedBin_IsInTheTotal_OnTheCard_AndInRecentActivity()
    {
        // Only a bool was kept, so a Tune-Up that permanently emptied gigabytes from the bin reported "Freed 120 MB",
        // and Recent activity never said that the bin had been emptied.
        var result = Ran(emptied: true, new RecycleBinContents(3L << 30, 12));

        Assert.Equal("120.0 MB", result.FreedDisplay);
        Assert.Equal("3.1 GB", result.TotalFreedDisplay);
        Assert.Equal("Recycle Bin emptied (12 items, 3.0 GB).", result.RecycleBinDisplay);
        Assert.Equal("Freed 120.0 MB of temporary files and permanently emptied the Recycle Bin (12 items, 3.0 GB)",
            result.ActivityDetail);
    }

    [Fact]
    public void AnAlreadyEmptyBin_AddsNothing_AndIsSaidToBeEmpty()
    {
        var result = Ran(emptied: true, new RecycleBinContents(0, 0));

        Assert.Equal("120.0 MB", result.TotalFreedDisplay);
        Assert.Equal("The Recycle Bin was already empty.", result.RecycleBinDisplay);
        Assert.Equal("Freed 120.0 MB of temporary files", result.ActivityDetail);
    }

    [Fact]
    public void ABinThatCouldNotBeEmptied_IsSaidSo_AndNotCounted()
    {
        var result = Ran(emptied: false, new RecycleBinContents(3L << 30, 12));

        Assert.Equal("120.0 MB", result.TotalFreedDisplay);
        Assert.Equal("The Recycle Bin could not be emptied.", result.RecycleBinDisplay);
        Assert.Equal("Freed 120.0 MB of temporary files", result.ActivityDetail);
    }

    [Fact]
    public void ABinEmptiedWithoutAMeasurement_IsStillRecorded()
    {
        var result = Ran(emptied: true, before: null);

        Assert.Equal("120.0 MB", result.TotalFreedDisplay);
        Assert.Equal("Recycle Bin emptied.", result.RecycleBinDisplay);
        Assert.Equal("Freed 120.0 MB of temporary files and permanently emptied the Recycle Bin", result.ActivityDetail);
    }

    [Fact]
    public void ABinTheTuneUpWasNotAskedToEmpty_HasNoLineOnTheCard()
        => Assert.Equal("", (Ran(emptied: false, before: null) with { RecycleBinSkipped = true }).RecycleBinDisplay);

    // ── The temp sweep leaves every temp root's extraction folder alone ───────

    [Fact]
    public void TheTempWalk_LeavesTheExtractionFolderAtTheTopOfAnyTempRootAlone()
    {
        // The sweep walks %WinDir%\Temp too, where a single-file app running as SYSTEM unpacks into .net, and the
        // walk excluded only the current user's. Walked here over a folder standing in for that temp root.
        var root = Path.Combine(Path.GetTempPath(), "SysManagerTempWalk_" + Guid.NewGuid().ToString("N"));
        var unpacked = Path.Combine(root, ".net", "SomeService", "hash");
        Directory.CreateDirectory(unpacked);
        File.WriteAllText(Path.Combine(unpacked, "native.dll"), "x");
        File.WriteAllText(Path.Combine(root, "old.tmp"), "x");

        try
        {
            var files = SafeFileWalk.Files(root, CancellationToken.None, TuneUpService.TempWalk).ToList();
            var folders = SafeFileWalk.DirectoriesDeepestFirst(root, CancellationToken.None, TuneUpService.TempWalk);

            Assert.Equal([Path.Combine(root, "old.tmp")], files);
            Assert.Empty(folders);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── The temp sweep leaves what was created or changed in the last week (#2890) ───────
    // Being held open was the only thing that kept a temp file, so an installer that had unpacked its files and not
    // opened them yet lost them part-way. Swept here over a folder of the test's own, never the real temp folders.

    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MonthAgo = Now.AddDays(-30);

    private static string Aged(string path, DateTime created, DateTime written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    /// <summary>A temp folder with one file of each age, and the folders the sweep has to decide on.</summary>
    private sealed class AgedTemp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SysManagerTempAge_" + Guid.NewGuid().ToString("N"));
        public string Unpacked { get; }    // created now, by an installer, keeping its archive time
        public string Fresh { get; }       // created and written now
        public string Rewritten { get; }   // created long ago, written now
        public string Leftover { get; }    // neither, a month ago: the one the sweep is for
        public string OldFolder { get; }
        public string NewEmptyFolder { get; }

        public AgedTemp()
        {
            Unpacked = Aged(Path.Combine(Root, "setup-1234", "payload.cab"), Now, MonthAgo);
            Fresh = Aged(Path.Combine(Root, "fresh.tmp"), Now, Now);
            Rewritten = Aged(Path.Combine(Root, "log.txt"), MonthAgo, Now);
            OldFolder = Path.Combine(Root, "stale");
            Leftover = Aged(Path.Combine(OldFolder, "leftover.tmp"), MonthAgo, MonthAgo);
            Directory.SetCreationTimeUtc(OldFolder, MonthAgo);
            NewEmptyFolder = Path.Combine(Root, "empty-new");
            Directory.CreateDirectory(NewEmptyFolder);
            Directory.SetCreationTimeUtc(NewEmptyFolder, Now);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void TheTempSweep_LeavesWhatWasCreatedOrChangedThisWeek_AndCleansTheRest()
    {
        using var temp = new AgedTemp();

        var (_, deleted, errors, _) = TuneUpService.CleanTempFiles([temp.Root], Now, CancellationToken.None);

        Assert.Equal((1, 0), (deleted, errors));
        Assert.False(File.Exists(temp.Leftover));
        Assert.All(new[] { temp.Unpacked, temp.Fresh, temp.Rewritten }, file => Assert.True(File.Exists(file), file));
        Assert.False(Directory.Exists(temp.OldFolder));          // emptied, and made a month ago
        Assert.True(Directory.Exists(temp.NewEmptyFolder));      // empty, but made just now
    }

    [Fact]
    public void TheFilesQuickCleanupMeasures_AreTheFilesTheSweepCleans()
    {
        using var temp = new AgedTemp();

        var counted = TuneUpService.TempFilesToClean(temp.Root, Now - CleanupAge.TempFilesKeptFor, CancellationToken.None);

        Assert.Equal([temp.Leftover], counted);
    }

    // ---------- a cancelled sweep, and a cancelled Tune-Up (#2736) ----------
    // A cancel threw out of the sweep, and the count of what it had already deleted went with the exception, so Quick
    // Cleanup, the Dashboard and the Quick Tune-Up said only that they were cancelled and recorded nothing. What was
    // deleted is gone for good either way.

    /// <summary>Two temp roots that cancel the sweep once it asks for the second, so the cancel lands between them.</summary>
    private sealed class CancelsAfterTheFirst(string first, string second, CancellationTokenSource cts) : IReadOnlyList<string>
    {
        public int Count => 2;
        public string this[int index] => index == 0 ? first : second;

        public IEnumerator<string> GetEnumerator()
        {
            yield return first;
            cts.Cancel();
            yield return second;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void TheTempSweep_CancelledPartWay_ReturnsWhatItHadDeleted_InsteadOfThrowing()
    {
        using var first = new AgedTemp();
        using var second = new AgedTemp();
        using var cts = new CancellationTokenSource();

        var result = TuneUpService.CleanTempFiles(new CancelsAfterTheFirst(first.Root, second.Root, cts), Now, cts.Token);

        Assert.Equal(new TempCleanResult(1, 1, 0, Cancelled: true), result);
        Assert.False(File.Exists(first.Leftover));
        Assert.True(File.Exists(second.Leftover), "the sweep went on after the cancel");
    }

    [Fact]
    public void TheTempSweep_ThatFinished_IsNotMarkedCancelled()
    {
        using var temp = new AgedTemp();

        var result = TuneUpService.CleanTempFiles([temp.Root], Now, CancellationToken.None);

        Assert.Equal(new TempCleanResult(1, 1, 0, Cancelled: false), result);
    }

    /// <summary>A step the Tune-Up must not reach: the exception is one no step catches, so reaching it fails the test.</summary>
    private static Func<Task<T>> NotReached<T>() => () => throw new NotSupportedException("a step after the cancel ran");

    private static readonly RecycleBinContents TwoItems = new(2048, 2);

    [Fact]
    public async Task ATuneUpCancelledDuringTheTempSweep_ReturnsWhatItFreed_AndLeavesTheBin()
    {
        using var cts = new CancellationTokenSource();
        var binAsked = false;

        var result = await TuneUpService.RunStepsAsync(
            () => { cts.Cancel(); return Task.FromResult(new TempCleanResult(4096, 3, 1, Cancelled: true)); },
            () => { binAsked = true; return new RecycleBinEmptyResult(true, TwoItems); },
            NotReached<ShortcutScanReport>(), NotReached<IReadOnlyList<DiskHealthReport>>(), NotReached<SystemSnapshot>(),
            emptyRecycleBin: true, progress: null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal((4096L, 3, 1), (result.TempBytesFreed, result.TempFilesDeleted, result.TempErrors));
        Assert.False(binAsked);
        Assert.False(result.RecycleBinEmptied);
        Assert.True(result.RemovedAnything);
    }

    [Fact]
    public async Task ATuneUpCancelledWhileTheBinWasEmptied_ReturnsTheBinToo()
    {
        // The shell empties the bin to the end once asked, so a cancel then is seen after it.
        using var cts = new CancellationTokenSource();

        var result = await TuneUpService.RunStepsAsync(
            () => Task.FromResult(new TempCleanResult(4096, 3, 0, Cancelled: false)),
            () => { cts.Cancel(); return new RecycleBinEmptyResult(true, TwoItems); },
            NotReached<ShortcutScanReport>(), NotReached<IReadOnlyList<DiskHealthReport>>(), NotReached<SystemSnapshot>(),
            emptyRecycleBin: true, progress: null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.RecycleBinEmptied);
        Assert.Equal(TwoItems, result.RecycleBinBefore);
        Assert.Equal("Freed 4.0 KB of temporary files and permanently emptied the Recycle Bin (2 items, 2.0 KB)",
            result.ActivityDetail);
    }

    [Fact]
    public async Task ATuneUpCancelledDuringACheck_StillReturnsWhatItRemoved()
    {
        using var cts = new CancellationTokenSource();

        var result = await TuneUpService.RunStepsAsync(
            () => Task.FromResult(new TempCleanResult(4096, 3, 0, Cancelled: false)),
            () => new RecycleBinEmptyResult(true, TwoItems),
            () => { cts.Cancel(); return Task.FromCanceled<ShortcutScanReport>(cts.Token); },
            NotReached<IReadOnlyList<DiskHealthReport>>(), NotReached<SystemSnapshot>(),
            emptyRecycleBin: true, progress: null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(3, result.TempFilesDeleted);
        Assert.Equal(TwoItems, result.RecycleBinBefore);
    }

    [Fact]
    public async Task ATuneUpThatFinished_IsNotMarkedCancelled()
    {
        var result = await TuneUpService.RunStepsAsync(
            () => Task.FromResult(new TempCleanResult(4096, 3, 0, Cancelled: false)),
            () => new RecycleBinEmptyResult(true, TwoItems),
            () => throw new IOException("no shortcuts here"),
            () => throw new InvalidOperationException("no disks here"),
            () => throw new InvalidOperationException("no WMI here"),
            emptyRecycleBin: true, progress: null, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.Equal(["shortcuts", "the disks", "memory", "uptime"], result.NotChecked);
    }

    [Fact]
    public async Task ACancelTheTuneUpDidNotAskFor_StillEndsItWithAnException()
    {
        // Only the Tune-Up's own cancel returns a result: a step that gave up by itself is not a cancel by the user.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TuneUpService.RunStepsAsync(
            () => Task.FromResult(new TempCleanResult(4096, 3, 0, Cancelled: false)),
            () => new RecycleBinEmptyResult(true, TwoItems),
            () => Task.FromCanceled<ShortcutScanReport>(new CancellationToken(canceled: true)),
            NotReached<IReadOnlyList<DiskHealthReport>>(), NotReached<SystemSnapshot>(),
            emptyRecycleBin: true, progress: null, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, false, null, false)]   // nothing deleted, and the bin left
    [InlineData(3, false, null, true)]    // temp files deleted
    [InlineData(0, true, 2L, true)]       // the bin emptied of what it held
    [InlineData(0, true, 0L, false)]      // the bin was already empty
    [InlineData(0, true, null, true)]     // emptied, and the shell could not say of what
    public void RemovedAnything_SaysWhetherTheTuneUpDeletedSomething(int files, bool binEmptied, long? binItems, bool removed)
    {
        var result = new TuneUpResult
        {
            TempFilesDeleted = files,
            RecycleBinEmptied = binEmptied,
            RecycleBinBefore = binItems is { } items ? new RecycleBinContents(items * 1024, items) : null,
        };

        Assert.Equal(removed, result.RemovedAnything);
    }
}
