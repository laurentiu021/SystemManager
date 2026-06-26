// SysManager · DuplicateFileViewModel — find duplicate files
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Duplicate File Finder tab — scans a folder for files with identical
/// content and shows them grouped by hash. Read-only: only "Show in
/// Explorer" and "Copy path" are offered.
/// </summary>
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

    public DuplicateFileViewModel(DuplicateFileService service)
    {
        _service = service;
        // Resolve known-folder paths + probe drives off the UI thread: DriveInfo.IsReady can
        // stall on a disconnected mapped/removable volume, which would freeze startup since
        // this VM is built eagerly. The collection update runs back on the UI thread.
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
                StatusMessage = $"{p.Phase} — {p.FilesDiscovered:N0} found, {p.FilesHashed:N0} hashed";
            });

            var results = await _service.ScanAsync(SelectedFolder, minBytes, progress, ct);

            Groups.ReplaceWith(results);

            GroupCount = Groups.Count;
            DuplicateFileCount = Groups.Sum(g => g.Files.Count);
            TotalWasted = Groups.Sum(g => g.WastedBytes);

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

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
