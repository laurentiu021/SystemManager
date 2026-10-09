// SysManager · DiskAnalyzerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskAnalyzerViewModel"/>. Verifies initial state,
/// presets, and command availability.
/// </summary>
// Serialized: running AnalyzeCommand takes the process-wide Disk lock, which the classes in the collection
// hold and assert on. Run in parallel with them, a scan here made their Disk lock look taken.
[Collection("ProcessWideStatics")]
public class DiskAnalyzerViewModelTests
{
    // The VM resolves its preset paths asynchronously off the UI thread (DriveInfo probing
    // can stall, so it's moved off startup); wait for that init so the preset assertions
    // observe the populated collection instead of racing the background load.
    private static DiskAnalyzerViewModel NewVm()
    {
        var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(),
            new DiskScanHistoryService(Path.Combine(Path.GetTempPath(),
                "SysManagerDiskHistVm_" + Guid.NewGuid().ToString("N"))),
            TempPreferences());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>A preference store in a folder of its own, so no test reads or writes the real one.</summary>
    private static DiskAnalyzerPreferenceService TempPreferences(string? dir = null) =>
        new(dir ?? Path.Combine(Path.GetTempPath(), "SysManagerDiskPrefVm_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        // TotalFolders was asserted here too. It was computed from a full recursive
        // Entries.Sum(e => e.FolderCount) on every scan and read by nothing — no binding, no other
        // code — and this assertion on its default value is what made it look exercised.
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.TotalSize);
        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal(0, vm.EntryCount);
        Assert.Empty(vm.Entries);
        Assert.Contains("Select", vm.ScanSummary);
    }

    [Fact]
    public void Constructor_PresetPaths_NotEmpty()
    {
        var vm = NewVm();
        Assert.NotEmpty(vm.PresetPaths);
    }

    [Fact]
    public void Constructor_SelectedPath_IsSet()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedPath));
    }

    [Fact]
    public void Constructor_PresetPaths_ContainFixedDrives()
    {
        var vm = NewVm();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var drive in drives)
            Assert.Contains(vm.PresetPaths, p => p == drive);
    }

    [Fact]
    public void AnalyzeCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.AnalyzeCommand);
    }

    [Fact]
    public void CancelAnalysisCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.CancelAnalysisCommand);
    }

    [Fact]
    public void ShowInExplorerCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ShowInExplorerCommand);
    }

    [Fact]
    public void DrillDownCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.DrillDownCommand);
    }

    [Fact]
    public void GoUpCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.GoUpCommand);
    }

    [Fact]
    public void BrowseFolderCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.BrowseFolderCommand);
    }

    [Fact]
    public void SelectedPath_CanBeChanged()
    {
        var vm = NewVm();
        vm.SelectedPath = @"C:\Test";
        Assert.Equal(@"C:\Test", vm.SelectedPath);
    }

    [Fact]
    public void HasDriveInfo_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.HasDriveInfo);
    }

    // ── Empty state distinguishes "not run yet" from "ran, found nothing" ───

    [Fact]
    public void BeforeAnyScan_EmptyState_TellsTheUserToScan()
    {
        var vm = NewVm();
        Assert.False(vm.HasScanned);
        Assert.Equal("No results yet", vm.EmptyTitle);
        Assert.Contains("analyze", vm.EmptyMessage);
    }

    [Fact]
    public async Task AfterAScanThatFoundNothing_EmptyState_StopsAskingForAScan()
    {
        // The overlay used to hardcode "No results yet … Pick a folder and analyze", so a
        // completed zero-result scan told the user to do the thing they had just done — while the
        // summary card next to it correctly said "No subfolders found." Scan a genuinely empty
        // directory and assert the two no longer contradict each other.
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = NewVm();
            vm.SelectedPath = dir;

            await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.Empty(vm.Entries);                       // nothing found, so the overlay shows
            Assert.True(vm.HasScanned);
            Assert.Equal("Nothing to show", vm.EmptyTitle);
            Assert.DoesNotContain("Pick a folder", vm.EmptyMessage);
            Assert.Equal("No subfolders found.", vm.ScanSummary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HasScanned_RaisesChangeNotificationsForTheEmptyStateText()
    {
        // The [NotifyPropertyChangedFor] attributes are what actually refresh the overlay; without
        // them the computed strings would change but the bound EmptyState would keep the old text.
        var vm = NewVm();
        var raised = vm.RecordPropertyChanges();

        vm.HasScanned = true;

        Assert.Contains(nameof(vm.EmptyTitle), raised);
        Assert.Contains(nameof(vm.EmptyMessage), raised);
    }

    // ── A folder that could not be measured (#2504) ──
    //
    // It used to arrive as an empty scan: "No subfolders found.", "Analysis complete." and a completion toast, and the
    // empty result was saved as the folder's latest scan, so the next real one read as "larger than your last scan".

    // What the history holds for a root once it could be read (#2521).
    private static async Task<DiskScanSnapshot?> FoundAsync(DiskScanHistoryService history, string root)
    {
        var (readable, snapshot) = await history.FindAsync(root);
        Assert.True(readable, "the history could not be read");
        return snapshot;
    }

    private static (DiskAnalyzerViewModel Vm, DiskScanHistoryService History) NewVmWithHistory()
    {
        var history = new DiskScanHistoryService(Path.Combine(Path.GetTempPath(),
            "SysManagerDiskHistVm_" + Guid.NewGuid().ToString("N")));
        var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(), history, TempPreferences());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return (vm, history);
    }

    [Fact]
    public async Task AFolderThatNoLongerExists_SaysSo_AndRecordsNoScan()
    {
        var (vm, history) = NewVmWithHistory();
        var missing = Path.Combine(Path.GetTempPath(), "SysManagerTests", "gone_" + Guid.NewGuid().ToString("N"));
        vm.SelectedPath = missing;

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(DiskAnalyzerService.AnalysisFailure.NotFound, vm.LastFailure);
        Assert.Equal("This folder could not be measured", vm.EmptyTitle);
        Assert.StartsWith("It no longer exists.", vm.EmptyMessage);
        Assert.Equal("This folder could not be measured.", vm.ScanSummary);
        Assert.DoesNotContain("complete", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasScanned);
        Assert.Null(await FoundAsync(history, missing));
    }

    [Fact]
    public async Task AScan_WhenTheEarlierScansCannotBeRead_SaysSo_AndRecordsNothingOverThem()
    {
        // The earlier scans used to read as none, so the trend line looked like a first scan and the save wrote
        // this scan over every other folder's (#2521).
        var historyDir = Path.Combine(Path.GetTempPath(), "SysManagerDiskHistVm_" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "data.bin"), new byte[40_000]);
        try
        {
            var history = new DiskScanHistoryService(historyDir);
            Assert.True(await history.SaveAsync(new DiskScanSnapshot
            {
                RootPath = @"C:\Other",
                TotalSize = 1,
                CapturedAt = new DateTime(2026, 1, 1),
                TopFolders = [],
            }));
            var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(), history, TempPreferences());
            await vm.InitializationComplete;
            vm.SelectedPath = dir;

            // Held open with delete sharing only: the reads fail, and a write would still succeed.
            using (new FileStream(Path.Combine(historyDir, "disk-scan-history.json"),
                       FileMode.Open, FileAccess.Read, FileShare.Delete))
                await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.True(vm.HasScanned);
            Assert.Equal(DiskAnalyzerViewModel.HistoryUnreadable, vm.TrendSummary);
            Assert.NotNull(await FoundAsync(history, @"C:\Other"));
            Assert.Null(await FoundAsync(history, dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(historyDir)) Directory.Delete(historyDir, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedScan_LeavesTheFoldersLastRealScanInPlace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "data.bin"), new byte[40_000]);
        var moved = dir + "_moved";
        try
        {
            var (vm, history) = NewVmWithHistory();
            vm.SelectedPath = dir;
            await vm.AnalyzeCommand.ExecuteAsync(null);
            var recorded = await FoundAsync(history, dir);
            Assert.NotNull(recorded);   // the premise: the real scan was recorded

            Directory.Move(dir, moved);   // now it "no longer exists"
            await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.Equal(DiskAnalyzerService.AnalysisFailure.NotFound, vm.LastFailure);
            var after = await FoundAsync(history, dir);
            Assert.NotNull(after);
            Assert.Equal(recorded.TotalSize, after.TotalSize);
            Assert.Equal(recorded.CapturedAt, after.CapturedAt);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }

    [Fact]
    public async Task AScanThatWorksAfterAFailedOne_ClearsTheFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, _) = NewVmWithHistory();
            vm.SelectedPath = dir;
            await vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.Equal(DiskAnalyzerService.AnalysisFailure.NotFound, vm.LastFailure);   // the premise

            Directory.CreateDirectory(dir);
            await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.Equal(DiskAnalyzerService.AnalysisFailure.None, vm.LastFailure);
            Assert.Equal("Nothing to show", vm.EmptyTitle);
            Assert.Equal("No subfolders found.", vm.ScanSummary);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DescribeFailure_SaysSomethingDifferentForEachReason()
    {
        var notFound = DiskAnalyzerViewModel.DescribeFailure(DiskAnalyzerService.AnalysisFailure.NotFound);
        var isLink = DiskAnalyzerViewModel.DescribeFailure(DiskAnalyzerService.AnalysisFailure.IsLink);
        var unreadable = DiskAnalyzerViewModel.DescribeFailure(DiskAnalyzerService.AnalysisFailure.Unreadable);

        Assert.Contains("no longer exists", notFound, StringComparison.Ordinal);
        Assert.Contains("link", isLink, StringComparison.Ordinal);
        Assert.Contains("did not let SysManager list", unreadable, StringComparison.Ordinal);
        Assert.Equal("", DiskAnalyzerViewModel.DescribeFailure(DiskAnalyzerService.AnalysisFailure.None));
    }

    // ── Exclusion disclosure (the total is partial by design) ────────────────────────────────
    //
    // Four Windows subtrees are skipped because they are slow or unreadable, and junctions are never
    // followed. Windows\WinSxS alone is routinely several GB, so a user comparing this total against
    // the free space Windows reports sees a multi-gigabyte gap. Nothing in the tab said so.

    [Fact]
    public void ExclusionNote_SaysTheTotalCanBeSmallerThanWindowsReports()
    {
        // The one sentence that stops the number reading as a bug.
        var note = NewVm().ExclusionNote;

        Assert.False(string.IsNullOrWhiteSpace(note));
        Assert.Contains("aren't counted", note);
        Assert.Contains("smaller than the space Windows reports", note);
    }

    [Fact]
    public void ExclusionDetail_NamesEveryFolderTheServiceActuallySkips()
    {
        // Derived from DiskAnalyzerService.ExcludedFolderNames rather than retyped, so the tooltip
        // cannot drift from the real SkipSegments list. Adding a fifth exclusion without updating the
        // disclosure fails here.
        var detail = NewVm().ExclusionDetail;

        Assert.NotEmpty(Services.DiskAnalyzerService.ExcludedFolderNames);
        foreach (var name in Services.DiskAnalyzerService.ExcludedFolderNames)
            Assert.Contains(name, detail);
    }

    [Fact]
    public void ExclusionDetail_ExplainsWhyJunctionsAreSkipped()
    {
        // The reparse-point guard is a correctness property, not an oversight — say why, so it does
        // not read as a missing feature.
        var detail = NewVm().ExclusionDetail;

        Assert.Contains("junction", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("double-count", detail);
    }

    [Fact]
    public void ExclusionText_IsInstanceNotStatic_SoItActuallyBinds()
    {
        // A {Binding} to a static member resolves to nothing and renders EMPTY — reintroducing exactly
        // the silence this change fixes, while still compiling and still passing any test that read the
        // property straight off the type. Nothing in Views/ uses x:Static, so instance is also uniform.
        foreach (var name in new[] { nameof(DiskAnalyzerViewModel.ExclusionNote),
                                     nameof(DiskAnalyzerViewModel.ExclusionDetail) })
        {
            var prop = typeof(DiskAnalyzerViewModel).GetProperty(name);
            Assert.NotNull(prop);
            Assert.False(prop!.GetGetMethod()!.IsStatic, $"{name} must be an instance property to bind.");
        }
    }

    [Fact]
    public void DiskAnalyzerView_ShowsTheExclusionNote()
    {
        // The defect was that nothing in the tab disclosed it — a grep for excluded/skip/system area in
        // the view returned 0. Asserting the ViewModel alone would pass on the unfixed code.
        var xaml = File.ReadAllText(TestPaths.AppFile("Views", "DiskAnalyzerView.xaml"));

        Assert.Contains("ExclusionNote", xaml);
        Assert.Contains("ExclusionDetail", xaml);   // the hover naming the exact folders
    }

    // ---------- the "since last scan" delta wording (#1591) ----------
    // DescribeTrend is the whole value of the feature: turning a one-off number into "what changed?".
    // Tested at the source, so the branches are deterministic — no disk, no wall-clock.

    private static DiskScanSnapshot Prior(long total, DateTime at) =>
        new() { RootPath = @"C:\Data", TotalSize = total, CapturedAt = at };

    [Fact]
    public void Trend_WithNoPriorScan_IsEmpty()
    {
        // A first-ever scan of a root must show nothing, not "0 bytes larger".
        Assert.Equal("", DiskAnalyzerViewModel.DescribeTrend(null, 5_000_000_000));
    }

    [Fact]
    public void Trend_WhenLarger_SaysLargerAndNamesTheDate()
    {
        var prior = Prior(2_000_000_000, new DateTime(2026, 7, 12));
        var text = DiskAnalyzerViewModel.DescribeTrend(prior, 5_200_000_000);

        Assert.Contains("larger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("smaller", text, StringComparison.Ordinal);
        Assert.Contains("12 Jul 2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_WhenSmaller_SaysSmaller()
    {
        var prior = Prior(5_000_000_000, new DateTime(2026, 7, 12));
        var text = DiskAnalyzerViewModel.DescribeTrend(prior, 1_000_000_000);

        Assert.Contains("smaller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("larger", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_WhenNegligiblyChanged_ReadsAsAboutTheSame()
    {
        // A few kilobytes on a multi-gigabyte folder is churn, not growth — it must not read as either.
        var prior = Prior(5_000_000_000, new DateTime(2026, 7, 12));
        var text = DiskAnalyzerViewModel.DescribeTrend(prior, 5_000_064_000);

        Assert.Contains("About the same", text, StringComparison.Ordinal);
        Assert.DoesNotContain("larger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("smaller", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_ExactlyUnchanged_ReadsAsAboutTheSame()
    {
        var prior = Prior(5_000_000_000, new DateTime(2026, 7, 12));
        Assert.Contains("About the same",
            DiskAnalyzerViewModel.DescribeTrend(prior, 5_000_000_000), StringComparison.Ordinal);
    }

    // ── The announced half vs the shown half (#2274) ──
    // The status line is a live region, and the scan reported into it once per top-level subfolder with no
    // rate limit at all. A folder with hundreds of small children announced hundreds of sentences in a few
    // seconds, so the tab said LESS the more there was to say. The per-folder value now goes to a silent
    // readout and the status line keeps the coarse phase, exactly as Duplicate Finder's was split in #2143.

    private static DiskAnalyzerService.AnalysisProgress Tick(int scanned, string folder) =>
        new(scanned, folder);

    [Fact]
    public void TheAnnouncedLine_IsNotTouched_WhileTheReadoutChangesEveryFolder()
    {
        var vm = NewVm();
        var before = vm.StatusMessage;
        var raised = vm.RecordPropertyChanges();

        // What analysing a user profile looks like: one report per child directory, as fast as the walk
        // reaches them.
        for (var i = 1; i <= 40; i++)
            vm.ApplyScanProgress(Tick(i, $@"C:\Sample\folder{i}"));

        // Not "changed twice" — not at all. This callback has no coarse value to contribute: the phase
        // boundaries are the command's own, so every announcement this tab makes is one of the three
        // sentences it says when a scan starts or ends.
        Assert.DoesNotContain(nameof(vm.StatusMessage), raised);
        Assert.Equal(before, vm.StatusMessage);

        // …while the eye still gets every report. Without this half the test would also pass on a callback
        // that had simply stopped reporting anything.
        Assert.Equal(40, raised.Count(n => n == nameof(vm.ScanReadout)));
    }

    [Fact]
    public void TheReadout_ShowsTheCount_AndTheFolderNameRatherThanItsPath()
    {
        var vm = NewVm();
        vm.ApplyScanProgress(Tick(1234, @"C:\Users\Sample\AppData\Local"));

        Assert.Equal("1,234 folders measured · Local", vm.ScanReadout);
        // The path goes to the hover instead: the count plus a deep path exceeds the row at a small window.
        Assert.Equal(@"C:\Users\Sample\AppData\Local", vm.CurrentFolder);
    }

    [Fact]
    public void TheSettlingReport_ClearsTheReadout_RatherThanNamingAFolderCalledDone()
    {
        // The service sends one last report with the settled count and no folder. It used to pass the literal
        // "Done" there, which the tab rendered as "Scanning folder 1234: Done" — a sentence claiming the scan
        // is still running, naming a folder that does not exist.
        var vm = NewVm();
        vm.ApplyScanProgress(Tick(1234, @"C:\Sample\Local"));
        Assert.NotEmpty(vm.ScanReadout);

        vm.ApplyScanProgress(Tick(1234, string.Empty));

        Assert.Equal("", vm.ScanReadout);
        Assert.Equal("", vm.CurrentFolder);
    }

    [Fact]
    public void AFolderPathEndingInASeparator_StillShowsSomethingAfterTheCount()
    {
        // Path.GetFileName returns "" for a trailing separator, which would leave the readout ending in a
        // bare separator. The service applies the same fallback when it names the entry itself.
        var vm = NewVm();
        vm.ApplyScanProgress(Tick(3, @"C:\Sample\"));

        Assert.Equal(@"3 folders measured · C:\Sample\", vm.ScanReadout);
    }

    [Fact]
    public void DiskAnalyzerView_AnnouncesTheStatusLine_AndShowsTheReadoutSilently()
    {
        // The assertions above would all pass on markup that still bound one combined line, and the
        // live-region rule itself is enforced in ArchitectureTests. What only the shipped markup can show is
        // which line each half landed on — so this reads the XAML, in the same shape as the equivalent test
        // on Duplicate Finder.
        var root = System.Xml.Linq.XDocument.Load(TestPaths.AppFile("Views", "DiskAnalyzerView.xaml")).Root;
        Assert.NotNull(root);

        System.Xml.Linq.XElement BoundTo(string property) =>
            Assert.Single(root!.Descendants(),
                e => e.Name.LocalName == "TextBlock"
                     && e.Attribute("Text")?.Value == $"{{Binding {property}}}");

        var coarse = BoundTo("StatusMessage");
        Assert.Equal("{StaticResource StatusLine}", coarse.Attribute("Style")?.Value);
        Assert.Null(coarse.Attribute("ToolTip"));

        var readout = BoundTo("ScanReadout");
        Assert.Equal("{Binding CurrentFolder}", readout.Attribute("ToolTip")?.Value);
        Assert.Equal("CharacterEllipsis", readout.Attribute("TextTrimming")?.Value);

        // Caption, not StatusLine: StatusLine IS Caption plus AutomationProperties.LiveSetting, so basing the
        // readout on it would look identical and quietly put the fast half back into the announcements.
        var inline = Assert.Single(readout.Elements()
            .Where(e => e.Name.LocalName == "TextBlock.Style")
            .SelectMany(e => e.Elements().Where(s => s.Name.LocalName == "Style")));
        Assert.Equal("{StaticResource Caption}", inline.Attribute("BasedOn")?.Value);

        // This tab no longer uses the shared footer, because that control deliberately has no second slot.
        // Asserting its absence pins the reason: a well-meaning "deduplicate this" would delete the readout.
        Assert.DoesNotContain(root!.Descendants(), e => e.Name.LocalName == "StatusFooter");
    }

    // ---------- the map (#1592) ----------

    private static DiskAnalyzerViewModel NewVmWith(DiskAnalyzerPreferenceService preferences)
    {
        var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(),
            new DiskScanHistoryService(Path.Combine(Path.GetTempPath(),
                "SysManagerDiskHistVm_" + Guid.NewGuid().ToString("N"))),
            preferences);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>A folder with three subfolders of 40, 20 and 10 KB and one loose file, for scans that need content.</summary>
    private static string FolderWithThreeSubfolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        foreach (var (name, bytes) in new[] { ("big", 40_000), ("middle", 20_000), ("small", 10_000) })
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            File.WriteAllBytes(Path.Combine(dir, name, "data.bin"), new byte[bytes]);
        }
        File.WriteAllBytes(Path.Combine(dir, "loose.bin"), new byte[1_000]);
        return dir;
    }

    [Fact]
    public void TheMap_IsShownUntilHidden_AndHidingIsRemembered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerDiskPrefVm_" + Guid.NewGuid().ToString("N"));
        var vm = NewVmWith(TempPreferences(dir));
        Assert.True(vm.ShowMap);
        Assert.Equal("Hide map", vm.MapToggleText);

        vm.ToggleMapCommand.Execute(null);

        Assert.False(vm.ShowMap);
        Assert.Equal("Show map", vm.MapToggleText);
        // The next visit, as after a restart.
        Assert.False(NewVmWith(TempPreferences(dir)).ShowMap);
    }

    [Fact]
    public void AHiddenMap_ThatCannotBeRemembered_SaysSo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerDiskPrefVm_" + Guid.NewGuid().ToString("N"));
        Assert.True(TempPreferences(dir).Save(new DiskAnalyzerPreference(true)));

        DiskAnalyzerViewModel vm;
        // Held with delete sharing only while the tab loads, so its preference cannot be read and must not be
        // written over.
        using (new FileStream(Path.Combine(dir, DiskAnalyzerPreferenceService.FileName),
                   FileMode.Open, FileAccess.Read, FileShare.Delete))
            vm = NewVmWith(TempPreferences(dir));

        vm.ToggleMapCommand.Execute(null);

        Assert.False(vm.ShowMap);
        Assert.Equal(ViewModelBase.ChangeNotSavedStatus, vm.StatusMessage);
    }

    [Fact]
    public async Task AScan_DrawsTheMap_FromTheListsOwnEntries()
    {
        var dir = FolderWithThreeSubfolders();
        try
        {
            var vm = NewVmWith(TempPreferences());
            vm.SelectedPath = dir;
            Assert.False(vm.Map.HasMap);

            await vm.AnalyzeCommand.ExecuteAsync(null);
            vm.Map.Width = 940;
            vm.Map.Height = 240;

            Assert.True(vm.Map.HasMap);
            var folders = vm.Map.Tiles.Where(t => !t.IsOther).ToList();
            Assert.Equal(new[] { "big", "middle", "small" }, folders.Select(t => t.Name));
            // The very objects the list shows, so a block opens exactly what the list's row would.
            Assert.All(folders, t => Assert.Contains(vm.Entries, e => ReferenceEquals(e, t.Entry)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AScanThatCannotMeasureTheFolder_TakesTheMapAway()
    {
        var dir = FolderWithThreeSubfolders();
        try
        {
            var vm = NewVmWith(TempPreferences());
            vm.SelectedPath = dir;
            await vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.True(vm.Map.HasMap);

            vm.SelectedPath = Path.Combine(dir, "gone");
            await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.False(vm.Map.HasMap);
            Assert.Empty(vm.Map.Tiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningABlock_OpensItsFolder_AsTheListsRowDoes()
    {
        var dir = FolderWithThreeSubfolders();
        Directory.CreateDirectory(Path.Combine(dir, "big", "inner"));
        File.WriteAllBytes(Path.Combine(dir, "big", "inner", "more.bin"), new byte[5_000]);
        try
        {
            var vm = NewVmWith(TempPreferences());
            vm.SelectedPath = dir;
            await vm.AnalyzeCommand.ExecuteAsync(null);
            vm.Map.Width = 940;
            vm.Map.Height = 240;
            var big = vm.Map.Tiles.First(t => t.Name == "big");

            await vm.DrillDownCommand.ExecuteAsync(big.Entry);

            Assert.Equal(Path.Combine(dir, "big"), vm.SelectedPath);
            Assert.Equal("inner", Assert.Single(vm.Map.Tiles, t => !t.IsOther).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- F5 (#2598) ----------

    [Fact]
    public void F5_AnalyzesTheFolderAgain()
    {
        var vm = NewVm();

        Assert.Same(vm.AnalyzeCommand, vm.RefreshOnF5);
    }
}
