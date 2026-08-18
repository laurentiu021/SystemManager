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

    // MinSizeKb_CanBeChanged was removed as a setter round-trip. The property's real consequence is
    // `var minBytes = MinSizeKb * 1024` in ScanAsync, and asserting THAT needs a guard the code does
    // not have yet (a large typed value overflows long and inverts the filter) — tracked separately
    // so this test-only change stays behaviour-neutral.

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
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

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
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

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
    public void ScanStatus_NamesTheFileBeingRead()
    {
        var status = DuplicateFileViewModel.BuildScanStatus(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 1_234, FilesHashed: 567, BytesProcessed: 0,
            CurrentFile: @"C:\Users\someone\Pictures\holiday-2019\DSC_0042.jpg",
            Phase: "Hashing"));

        Assert.Contains("Hashing", status);
        Assert.Contains("1,234 found", status);
        Assert.Contains("567 hashed", status);
        Assert.Contains("DSC_0042.jpg", status);

        // Only the name: a deep path would dominate a single-line status row. The full path is the tooltip.
        Assert.DoesNotContain("Pictures", status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScanStatus_OmitsTheFileWhenThereIsNone(string current)
    {
        // The discovery phase reports ticks before it has a file in hand; the line must not end in a
        // dangling separator.
        var status = DuplicateFileViewModel.BuildScanStatus(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 10, FilesHashed: 0, BytesProcessed: 0, CurrentFile: current, Phase: "Scanning"));

        Assert.Equal("Scanning — 10 found, 0 hashed", status);
    }

    [Fact]
    public void ScanStatus_HandlesAFolderPathWithATrailingSeparator()
    {
        // Path.GetFileName returns "" for a path ending in a separator, which would have shown nothing at
        // all after the separator. Discovery reports folders too.
        var status = DuplicateFileViewModel.BuildScanStatus(new Services.DuplicateFileService.ScanProgress(
            FilesDiscovered: 5, FilesHashed: 0, BytesProcessed: 0,
            CurrentFile: @"C:\Users\someone\Downloads\", Phase: "Scanning"));

        Assert.Contains("Downloads", status);
        Assert.DoesNotContain("· ·", status);
    }

    [Fact]
    public void DuplicateFileView_ShowsTheScanStatusWithTheFullPathOnHover()
    {
        // The pure formatter above would pass on the unfixed code, because the view model always built a
        // status string — what was missing was the file name in it, and the path anywhere at all. Only the
        // shipped markup can show the tooltip is wired.
        var xaml = File.ReadAllText(ViewPath("DuplicateFileView.xaml"));

        Assert.Contains("ToolTip=\"{Binding CurrentFile}\"", xaml);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml);
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
