// SysManager · DuplicateFileViewModel — find duplicate files
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Duplicate File Finder tab — scans a folder for files with identical
/// content and shows them grouped by hash. Read-only: only "Show in
/// Explorer" and "Copy path" are offered.
/// </summary>
public partial class DuplicateFileViewModel : ViewModelBase
{
    private readonly DuplicateFileService _service = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<DuplicateFileGroup> Groups { get; } = new();
    public ObservableCollection<string> PresetFolders { get; } = new();

    [ObservableProperty] private string _selectedFolder = "";
    [ObservableProperty] private long _minSizeKb = 1;
    [ObservableProperty] private long _totalWasted;
    [ObservableProperty] private int _groupCount;
    [ObservableProperty] private int _duplicateFileCount;
    [ObservableProperty] private string _scanSummary = "Select a folder and click Scan.";
    [ObservableProperty] private string _currentFile = "";

    public DuplicateFileViewModel()
    {
        PopulatePresets();
    }

    private void PopulatePresets()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };

        foreach (var f in folders)
            if (!string.IsNullOrEmpty(f) && Directory.Exists(f))
                PresetFolders.Add(f);

        // Add fixed drives
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed && d.IsReady)
                PresetFolders.Add(d.RootDirectory.FullName);

        if (PresetFolders.Count > 0)
            SelectedFolder = PresetFolders[0];
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder)) return;

        _cts?.Cancel();
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

            foreach (var g in results)
                Groups.Add(g);

            GroupCount = Groups.Count;
            DuplicateFileCount = Groups.Sum(g => g.Files.Count);
            TotalWasted = Groups.Sum(g => g.WastedBytes);

            ScanSummary = GroupCount == 0
                ? "No duplicates found."
                : $"{GroupCount} groups · {DuplicateFileCount} files · {FormatSize(TotalWasted)} wasted";
            StatusMessage = "Scan complete.";
            Log.Information("Duplicate scan completed: {Groups} groups, {Files} files, {Wasted} wasted",
                GroupCount, DuplicateFileCount, FormatSize(TotalWasted));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
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

    [RelayCommand]
    private static void ShowInExplorer(DuplicateFileEntry? entry)
    {
        if (entry == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.Path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private static void CopyPath(DuplicateFileEntry? entry)
    {
        if (entry == null) return;
        try { System.Windows.Clipboard.SetText(entry.Path); } catch { }
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

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
