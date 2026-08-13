// SysManager · DuplicateFileViewModel — find duplicate files
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Duplicate File Finder tab — scans a folder for files with identical content and shows them
/// grouped by hash.
/// </summary>
/// <remarks>
/// STILL NON-DESTRUCTIVE, deliberately: the actions are "Show in Explorer", "Copy path" and
/// "Keep this one". Nothing is deleted, moved or renamed. A wrong deletion here costs the user their
/// own photos and documents with no undo, which is the worst outcome this app can produce, so the tab
/// suggests a decision and leaves the acting to the user in Explorer.
/// </remarks>
public sealed partial class DuplicateFileViewModel : ViewModelBase
{
    private readonly DuplicateFileService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<DuplicateFileGroup> Groups { get; } = new();
    public ObservableCollection<string> PresetFolders { get; } = new();

    [ObservableProperty] private string _selectedFolder = "";
    [ObservableProperty] private long _minSizeKb = 1;
    [ObservableProperty] private long _totalWasted;
    [ObservableProperty] private int _groupCount;
    [ObservableProperty] private int _duplicateFileCount;
    [ObservableProperty] private string _scanSummary = "Select a folder and click Scan.";
    [ObservableProperty] private string _currentFile = "";

    // Distinguishes the un-run state from a completed zero-result scan so the big empty-state overlay
    // doesn't claim "No duplicates found" before the user has ever scanned. Set true only after a scan
    // actually completes (see ScanAsync); a cancelled/failed scan leaves it as-is.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private bool _hasScanned;

    public string EmptyTitle => HasScanned ? "No duplicates found" : "No scan yet";
    public string EmptyMessage => HasScanned
        ? "No files with identical content in the selected folder."
        : "Pick a folder and scan to find files with identical content.";

    public DuplicateFileViewModel(DuplicateFileService service)
    {
        _service = service;
        // Resolve known-folder paths + probe drives off the UI thread: DriveInfo.IsReady can stall on
        // a disconnected mapped/removable volume. This tab is LAZY — NavItem.ContentFactory builds it
        // on first open, not at startup (the eager set is Dashboard, DarkMode and About; see the list
        // above NavItems) — so the stall this avoids is on the first navigation into the tab, not at
        // app launch. The Task.Run is still required either way; the collection update runs back on
        // the UI thread.
        InitializeAsync(PopulatePresetsAsync);
    }

    private async Task PopulatePresetsAsync()
    {
        var folders = await Task.Run(EnumeratePresetFolders).ConfigureAwait(true);
        foreach (var f in folders)
            PresetFolders.Add(f);
        if (PresetFolders.Count > 0)
            SelectedFolder = PresetFolders[0];
    }

    private static List<string> EnumeratePresetFolders()
    {
        var result = new List<string>();
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Helpers.KnownFolders.GetDocumentsPath(),
            Helpers.KnownFolders.GetDesktopPath(),
            Helpers.KnownFolders.GetDownloadsPath(),
            Helpers.KnownFolders.GetPicturesPath(),
            Helpers.KnownFolders.GetMusicPath(),
            Helpers.KnownFolders.GetVideosPath(),
        };

        foreach (var f in folders.Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x)))
            result.Add(f);

        // Add fixed drives
        foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            result.Add(d.RootDirectory.FullName);

        return result;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder)) return;

        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Duplicate File Scan");
        if (opLock is null)
        {
            ScanSummary = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning…";
        Groups.Clear();
        TotalWasted = 0;
        GroupCount = 0;
        DuplicateFileCount = 0;

        try
        {
            var minBytes = MinSizeKb * 1024;
            var progress = new Progress<DuplicateFileService.ScanProgress>(p =>
            {
                CurrentFile = p.CurrentFile;
                StatusMessage = BuildScanStatus(p);
            });

            var results = await _service.ScanAsync(SelectedFolder, minBytes, progress, ct);

            // Suggest a keeper per group BEFORE binding, so no group is ever shown as N equal rows with
            // no hint which file is the original.
            foreach (var group in results)
                group.ApplySuggestedKeeper();

            Groups.ReplaceWith(results);

            GroupCount = Groups.Count;
            DuplicateFileCount = Groups.Sum(g => g.Files.Count);
            TotalWasted = Groups.Sum(g => g.WastedBytes);

            HasScanned = true;
            ScanSummary = GroupCount == 0
                ? "No duplicates found."
                : $"{GroupCount} groups · {DuplicateFileCount} files · {FormatSize(TotalWasted)} wasted";
            StatusMessage = "Scan complete.";
            ToastService.Instance.Show("Duplicate Scan complete", $"{GroupCount} groups, {FormatSize(TotalWasted)} wasted");
            Log.Information("Duplicate scan completed: {Groups} groups, {Files} files, {Wasted} wasted",
                GroupCount, DuplicateFileCount, FormatSize(TotalWasted));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (IOException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            CurrentFile = "";
        }
    }

    /// <summary>
    /// The scan's status line: phase, running counts, and the file currently being read.
    /// <para>The file name was reported by the service and assigned to <see cref="CurrentFile"/> on every
    /// progress tick, but nothing displayed it — so a scan of a large folder showed only rising numbers,
    /// with no sign of which file it was on or whether it had stalled on one. Only the name is shown; the
    /// full path goes in the row's tooltip, because a deep path would otherwise dominate the line.</para>
    /// <para>Pure and static so the formatting is testable without running a scan.</para>
    /// </summary>
    internal static string BuildScanStatus(DuplicateFileService.ScanProgress p)
    {
        var counts = string.Create(CultureInfo.InvariantCulture, $"{p.Phase} — {p.FilesDiscovered:N0} found, {p.FilesHashed:N0} hashed");

        // Path.GetFileName returns "" for a directory path ending in a separator, and the discovery phase
        // reports folders as well as files; fall back to the raw value rather than showing nothing.
        if (string.IsNullOrWhiteSpace(p.CurrentFile)) return counts;
        var name = Path.GetFileName(p.CurrentFile.TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? counts : $"{counts} · {name}";
    }

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand]
    private static void ShowInExplorer(DuplicateFileEntry? entry)
    {
        if (entry is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.Path}\"",
                UseShellExecute = true
            })?.Dispose();
        }
        catch (InvalidOperationException ex) { Log.Debug(ex, "Failed to open explorer for {Path}", entry.Path); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug(ex, "Failed to open explorer for {Path}", entry.Path); }
    }

    [RelayCommand]
    private static void CopyPath(DuplicateFileEntry? entry)
    {
        if (entry is null) return;
        try { System.Windows.Clipboard.SetText(entry.Path); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Debug(ex, "Failed to copy path to clipboard"); }
    }

    /// <summary>
    /// Moves the suggested keeper within a group. "Oldest wins" is only a heuristic — a copy that
    /// preserved its timestamp, or a cloud-sync rewrite, breaks it — so the user has to be able to
    /// disagree with it. The group is looked up from the entry rather than passed in, because the
    /// per-file DataTemplate binds to the entry.
    /// </summary>
    [RelayCommand]
    private void KeepThis(DuplicateFileEntry? entry)
    {
        if (entry is null) return;

        var group = Groups.FirstOrDefault(g => g.Files.Contains(entry));
        group?.SetKeeper(entry);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to scan for duplicates"
        };
        if (dialog.ShowDialog() == true)
        {
            SelectedFolder = dialog.FolderName;
            if (!PresetFolders.Contains(SelectedFolder))
                PresetFolders.Add(SelectedFolder);
        }
    }

    private static string FormatSize(long bytes) => Helpers.FormatHelper.FormatSize(bytes);
}
