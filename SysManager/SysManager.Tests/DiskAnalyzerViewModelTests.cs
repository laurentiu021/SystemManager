// SysManager · DiskAnalyzerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskAnalyzerViewModel"/>. Verifies initial state,
/// presets, and command availability.
/// </summary>
public class DiskAnalyzerViewModelTests
{
    // The VM resolves its preset paths asynchronously off the UI thread (DriveInfo probing
    // can stall, so it's moved off startup); wait for that init so the preset assertions
    // observe the populated collection instead of racing the background load.
    private static DiskAnalyzerViewModel NewVm()
    {
        var vm = new DiskAnalyzerViewModel(new Services.DiskAnalyzerService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.TotalSize);
        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal(0, vm.TotalFolders);
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
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.HasScanned = true;

        Assert.Contains(nameof(vm.EmptyTitle), raised);
        Assert.Contains(nameof(vm.EmptyMessage), raised);
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
        var xaml = File.ReadAllText(ViewPath("DiskAnalyzerView.xaml"));

        Assert.Contains("ExclusionNote", xaml);
        Assert.Contains("ExclusionDetail", xaml);   // the hover naming the exact folders
    }

    // Walks up from the test binaries to the app project — .xaml is not copied to the output.
    private static string ViewPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }
}
