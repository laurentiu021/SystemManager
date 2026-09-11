// SysManager · DuplicateFileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DuplicateFileViewModel"/>. Verifies initial state,
/// preset folders, and FormatSize logic.
/// </summary>
public class DuplicateFileViewModelTests
{
    // The VM resolves its preset folders asynchronously off the UI thread (known-folder +
    // DriveInfo probing can stall, so it's moved off startup); wait for that init so the
    // preset assertions observe the populated collection instead of racing the load.
    private static DuplicateFileViewModel NewVm()
    {
        var vm = new DuplicateFileViewModel(new Services.DuplicateFileService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.GroupCount);
        Assert.Equal(0, vm.DuplicateFileCount);
        Assert.Equal(0, vm.TotalWasted);
        Assert.Equal(1, vm.MinSizeKb);
        Assert.Empty(vm.Groups);
        Assert.Contains("Select a folder", vm.ScanSummary);
    }

    [Fact]
    public void Constructor_PresetFolders_NotEmpty()
    {
        var vm = NewVm();
        Assert.NotEmpty(vm.PresetFolders);
    }

    [Fact]
    public void Constructor_SelectedFolder_IsSet()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedFolder));
    }

    [Fact]
    public void Constructor_PresetFolders_ContainUserProfile()
    {
        var vm = NewVm();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(vm.PresetFolders, f => f == userProfile);
    }

    [Fact]
    public void Constructor_PresetFolders_ContainFixedDrives()
    {
        var vm = NewVm();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var drive in drives)
            Assert.Contains(vm.PresetFolders, f => f == drive);
    }

    [Fact]
    public void ScanCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanCommand);
    }

    [Fact]
    public void CancelScanCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.CancelScanCommand);
    }

    [Fact]
    public void ShowInExplorerCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ShowInExplorerCommand);
    }

    [Fact]
    public void CopyPathCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.CopyPathCommand);
    }

    [Fact]
    public void BrowseFolderCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.BrowseFolderCommand);
    }

    // ---------- the minimum-size threshold the user types ----------

    /// <summary>An ordinary value scales to bytes untouched — the bound must not disturb normal use.</summary>
    [Theory]
    [InlineData(1L, 1024L)]
    [InlineData(500L, 512_000L)]
    [InlineData(1024L, 1_048_576L)]
    public void MinBytesFor_ScalesAnOrdinaryValue(long kb, long expectedBytes)
        => Assert.Equal(expectedBytes, DuplicateFileViewModel.MinBytesFor(kb));

    /// <summary>
    /// A negative figure must not invert the filter. The scan skips a file with
    /// <c>fi.Length &lt; minSizeBytes</c>, so a negative threshold skips nothing: "only files above X"
    /// silently becomes "scan and hash EVERY file in the folder". One stray leading minus in an unbounded
    /// TextBox is all it takes — exactly the input that has to be bounded at the boundary rather than
    /// trusted.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-5_000L)]
    [InlineData(long.MinValue)]
    public void MinBytesFor_NeverReturnsANegativeThreshold(long kb)
        => Assert.Equal(0L, DuplicateFileViewModel.MinBytesFor(kb));

    /// <summary>
    /// A very large figure must not overflow into a negative threshold — the same inversion by a
    /// different route, since anything above <c>long.MaxValue / 1024</c> wraps when scaled. Pinned at the
    /// exact boundary and one past it, because an off-by-one in the bound is precisely what would let the
    /// wrap back in.
    /// </summary>
    [Fact]
    public void MinBytesFor_ClampsInsteadOfOverflowing()
    {
        // The boundary itself still scales exactly, with no wrap.
        Assert.Equal(DuplicateFileViewModel.MaxMinSizeKb * 1024,
                     DuplicateFileViewModel.MinBytesFor(DuplicateFileViewModel.MaxMinSizeKb));

        // One past it, and the most extreme value the TextBox can produce, both land on the boundary…
        Assert.Equal(DuplicateFileViewModel.MaxMinSizeKb * 1024,
                     DuplicateFileViewModel.MinBytesFor(DuplicateFileViewModel.MaxMinSizeKb + 1));
        Assert.Equal(DuplicateFileViewModel.MaxMinSizeKb * 1024,
                     DuplicateFileViewModel.MinBytesFor(long.MaxValue));

        // …and every result stays positive, which is the property that actually protects the scan.
        Assert.True(DuplicateFileViewModel.MinBytesFor(long.MaxValue) > 0);
    }

    [Fact]
    public void SelectedFolder_CanBeChanged()
    {
        var vm = NewVm();
        vm.SelectedFolder = @"C:\Test";
        Assert.Equal(@"C:\Test", vm.SelectedFolder);
    }

    // ── DuplicateFileEntry model ──

    [Fact]
    public void DuplicateFileEntry_DefaultValues()
    {
        var entry = new DuplicateFileEntry();
        Assert.Equal("", entry.Path);
        Assert.Equal("", entry.Name);
        Assert.Equal(0, entry.SizeBytes);
        Assert.False(entry.IsSelected);
    }

    [Fact]
    public void DuplicateFileEntry_PropertyChange_Notifies()
    {
        var entry = new DuplicateFileEntry();
        var changed = entry.RecordPropertyChanges();

        entry.Name = "test.bin";
        entry.Path = @"C:\test.bin";
        entry.SizeBytes = 1024;
        entry.IsSelected = true;

        Assert.Contains("Name", changed);
        Assert.Contains("Path", changed);
        Assert.Contains("SizeBytes", changed);
        Assert.Contains("IsSelected", changed);
    }

    // ── DuplicateFileGroup model ──

    [Fact]
    public void DuplicateFileGroup_PropertyChange_Notifies()
    {
        var group = new DuplicateFileGroup();
        var changed = group.RecordPropertyChanges();

        group.Hash = "ABC123";
        group.FileSize = 2048;
        group.Count = 3;

        Assert.Contains("Hash", changed);
        Assert.Contains("FileSize", changed);
        Assert.Contains("Count", changed);
    }

    [Fact]
    public void DuplicateFileGroup_Files_IsObservable()
    {
        var group = new DuplicateFileGroup();
        Assert.NotNull(group.Files);
        Assert.Empty(group.Files);

        group.Files.Add(new DuplicateFileEntry { Name = "test.bin" });
        Assert.Single(group.Files);
    }

    // ── Keep-this override (regression: IsSelected was declared and read by nothing) ──

    private static DuplicateFileGroup SeededGroup(DuplicateFileViewModel vm)
    {
        var group = new DuplicateFileGroup { FileSize = 1024 };
        group.Files.Add(new DuplicateFileEntry
        {
            Path = @"C:\a\photo.jpg",
            Name = "photo.jpg",
            LastModified = new DateTime(2019, 1, 1)
        });
        group.Files.Add(new DuplicateFileEntry
        {
            Path = @"C:\b\photo.jpg",
            Name = "photo.jpg",
            LastModified = new DateTime(2026, 1, 1)
        });
        group.Count = group.Files.Count;
        group.ApplySuggestedKeeper();
        vm.Groups.Add(group);
        return group;
    }

    [Fact]
    public void KeepThisCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.KeepThisCommand);
    }

    [Fact]
    public void KeepThis_MovesTheKeeperWithinTheOwningGroup()
    {
        // The per-file DataTemplate binds the ENTRY, so the VM has to find the group itself. If that
        // lookup failed the button would appear to do nothing.
        var vm = NewVm();
        var group = SeededGroup(vm);
        var newer = group.Files.Single(f => f.Path == @"C:\b\photo.jpg");

        vm.KeepThisCommand.Execute(newer);

        Assert.True(newer.IsSelected);
        Assert.Single(group.Files, f => f.IsSelected);
    }

    [Fact]
    public void KeepThis_WithNull_IsIgnored()
    {
        var vm = NewVm();
        var group = SeededGroup(vm);

        var ex = Record.Exception(() => vm.KeepThisCommand.Execute(null));

        Assert.Null(ex);
        Assert.Single(group.Files, f => f.IsSelected);   // the existing suggestion survives
    }

    [Fact]
    public void KeepThis_EntryFromNoLoadedGroup_IsIgnored()
    {
        var vm = NewVm();
        var group = SeededGroup(vm);
        var stale = new DuplicateFileEntry { Path = @"C:\gone\photo.jpg", Name = "photo.jpg" };

        var ex = Record.Exception(() => vm.KeepThisCommand.Execute(stale));

        Assert.Null(ex);
        Assert.False(stale.IsSelected);
        Assert.Single(group.Files, f => f.IsSelected);
    }

    [Fact]
    public void DuplicateFileView_ShowsTheKeeperAndTheRule()
    {
        // The defect was a property nothing read. Asserting the model alone would pass on the unfixed
        // code, so this checks the shipped markup renders the badge, offers the override, and states
        // the rule — plus that it still promises nothing is deleted.
        var xaml = File.ReadAllText(ViewPath("DuplicateFileView.xaml"));

        Assert.Contains("KeepLabel", xaml);                  // the badge
        Assert.Contains("KeepThisCommand", xaml);            // the override
        Assert.Contains("oldest", xaml);                     // the rule, stated
        Assert.Contains("Nothing is deleted", xaml);         // still non-destructive
        Assert.Contains("LastModified", xaml);               // so "oldest" is checkable by eye
    }

    // ── Scan progress ──
    // The service reported the file it was reading on every tick and the view model stored it, but no XAML
    // bound it: a scan of a large folder showed rising counts with no sign of which file it was on, or
    // whether it had stalled on one.

    [Fact]
    public void ScanReadout_NamesTheFileBeingRead()
    {
        var readout = DuplicateFileViewModel.BuildScanReadout(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 1_234, FilesHashed: 567, BytesProcessed: 0,
            CurrentFile: @"C:\Users\someone\Pictures\holiday-2019\DSC_0042.jpg",
            Phase: "Hashing files…"));

        Assert.Contains("1,234 found", readout);
        Assert.Contains("567 hashed", readout);
        Assert.Contains("DSC_0042.jpg", readout);

        // Only the name: a deep path would dominate a single-line status row. The full path is the tooltip.
        Assert.DoesNotContain("Pictures", readout);

        // The phase belongs to the announced line beside this one, so repeating it here would print the
        // same word on the row twice (#2143).
        Assert.DoesNotContain("Hashing", readout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScanReadout_OmitsTheFileWhenThereIsNone(string current)
    {
        // The discovery phase reports ticks before it has a file in hand; the line must not end in a
        // dangling separator.
        var readout = DuplicateFileViewModel.BuildScanReadout(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 10, FilesHashed: 0, BytesProcessed: 0, CurrentFile: current, Phase: "Scanning"));

        Assert.Equal("10 found, 0 hashed", readout);
    }

    [Fact]
    public void ScanReadout_HandlesAFolderPathWithATrailingSeparator()
    {
        // Path.GetFileName returns "" for a path ending in a separator, which would have shown nothing at
        // all after the separator. Discovery reports folders too.
        var readout = DuplicateFileViewModel.BuildScanReadout(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 5, FilesHashed: 0, BytesProcessed: 0,
            CurrentFile: @"C:\Users\someone\Downloads\", Phase: "Scanning"));

        Assert.Contains("Downloads", readout);
        Assert.DoesNotContain("· ·", readout);
    }

    // ── The announced half vs the shown half (#2143) ──
    // The status line is a live region and it used to carry the counts and the file name, so it changed
    // about five times a second for the whole scan: a screen reader began a new sentence before finishing
    // the last. The phase now goes to the announced line and the fast half to a silent one beside it.

    private static Services.DuplicateFileService.ScanProgress Tick(
        long discovered, long hashed, string file, string phase) =>
        new(discovered, hashed, BytesProcessed: hashed * 1024, CurrentFile: file, Phase: phase);

    [Fact]
    public void TheAnnouncedLine_ChangesOncePerPhase_WhileTheReadoutChangesEveryTick()
    {
        var vm = NewVm();
        var announced = vm.RecordChangesOf(nameof(vm.StatusMessage), () => vm.StatusMessage);
        var raised = vm.RecordPropertyChanges();

        // The shape the service produces: it throttles reports to one every 200 ms, so 40 of them is eight
        // seconds of scanning — a different file and higher counts on each.
        for (var i = 1; i <= 20; i++)
            vm.ApplyScanProgress(Tick(i, 0, $"file{i}.jpg", "Discovering files…"));
        for (var i = 1; i <= 20; i++)
            vm.ApplyScanProgress(Tick(20, i, $"file{i}.jpg", "Hashing files…"));

        // Two announcements for forty reports. The assertion is on the SEQUENCE, not a count: a count would
        // also pass if the line said the wrong two things.
        Assert.Equal(["Discovering files…", "Hashing files…"], announced);

        // …while the eye still gets every tick. Without this half the test would pass on a status line that
        // simply stopped reporting.
        Assert.Equal(40, raised.Count(n => n == nameof(vm.ScanReadout)));
    }

    [Fact]
    public void TheFinalReport_IsNotRendered_BecauseItsFileNameIsAPlaceholder()
    {
        // The service sends one last report with settled counts and "Done" where a file path goes. The lines
        // after the await say the same thing in a full sentence, so rendering it would show a file that does
        // not exist and announce completion twice.
        var vm = NewVm();
        vm.ApplyScanProgress(Tick(900, 900, "photo.jpg", "Hashing files…"));
        var status = vm.StatusMessage;
        var readout = vm.ScanReadout;

        vm.ApplyScanProgress(Tick(900, 900, "Done", "Complete"));

        Assert.Equal(status, vm.StatusMessage);
        Assert.Equal(readout, vm.ScanReadout);
        Assert.Equal("photo.jpg", vm.CurrentFile);
        Assert.DoesNotContain("Done", vm.ScanReadout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AReportWithNoPhase_LeavesTheAnnouncedLineSayingSomething(string phase)
    {
        // A live region set to an empty string announces nothing, so the tab would go silent mid-scan.
        var vm = NewVm();
        vm.ApplyScanProgress(Tick(5, 0, "file.jpg", phase));

        Assert.Equal("Scanning…", vm.StatusMessage);
    }

    [Fact]
    public void DuplicateFileView_AnnouncesThePhase_AndShowsTheReadoutSilently()
    {
        // The view model assertions above would pass on markup that still bound the old single line, and the
        // live-region rule itself is enforced in ArchitectureTests. What only the shipped markup can show is
        // which line each half landed on: the tooltip and the trimming belong to the readout now, because the
        // readout is what carries a file name.
        var root = System.Xml.Linq.XDocument.Load(ViewPath("DuplicateFileView.xaml")).Root;
        Assert.NotNull(root);

        System.Xml.Linq.XElement BoundTo(string property) =>
            Assert.Single(root!.Descendants(),
                e => e.Name.LocalName == "TextBlock"
                     && e.Attribute("Text")?.Value == $"{{Binding {property}}}");

        var coarse = BoundTo("StatusMessage");
        Assert.Equal("{StaticResource StatusLine}", coarse.Attribute("Style")?.Value);
        Assert.Null(coarse.Attribute("ToolTip"));

        var readout = BoundTo("ScanReadout");
        Assert.Equal("{Binding CurrentFile}", readout.Attribute("ToolTip")?.Value);
        Assert.Equal("CharacterEllipsis", readout.Attribute("TextTrimming")?.Value);

        // Caption, not StatusLine: StatusLine IS Caption plus AutomationProperties.LiveSetting, so basing the
        // readout on it would look identical and quietly put the fast half back into the announcements.
        var inline = Assert.Single(readout.Elements()
            .Where(e => e.Name.LocalName == "TextBlock.Style")
            .SelectMany(e => e.Elements().Where(s => s.Name.LocalName == "Style")));
        Assert.Equal("{StaticResource Caption}", inline.Attribute("BasedOn")?.Value);
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
