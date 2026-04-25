// SysManager · DiskAnalyzerViewModel — disk space breakdown
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
/// Disk Analyzer tab — shows space breakdown by top-level folders.
/// Read-only: only "Show in Explorer" is offered.
/// </summary>
public partial class DiskAnalyzerViewModel : ViewModelBase
{
    private readonly DiskAnalyzerService _service = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<DiskUsageEntry> Entries { get; } = new();
    public ObservableCollection<string> PresetPaths { get; } = new();

    [ObservableProperty] private string _selectedPath = "";
    [ObservableProperty] private string _scanSummary = "Select a drive or folder and click Analyze.";
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _totalFolders;
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private string _currentFolder = "";

    // Drive-level info
    [ObservableProperty] private long _driveTotal;
    [ObservableProperty] private long _driveFree;
    [ObservableProperty] private long _driveUsed;
    [ObservableProperty] private double _driveUsedPercent;
    [ObservableProperty] private bool _hasDriveInfo;

    public DiskAnalyzerViewModel()
    {
        PopulatePresets();
    }

    private void PopulatePresets()
    {
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed && d.IsReady)
                PresetPaths.Add(d.RootDirectory.FullName);

        var special = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var p in special)
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !PresetPaths.Contains(p))
                PresetPaths.Add(p);

        if (PresetPaths.Count > 0)
            SelectedPath = PresetPaths[0];
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Analyzing…";
        Entries.Clear();
        TotalSize = 0;
        TotalFiles = 0;
        TotalFolders = 0;
        EntryCount = 0;

        UpdateDriveInfo();

        try
        {
            var progress = new Progress<DiskAnalyzerService.AnalysisProgress>(p =>
            {
                CurrentFolder = p.CurrentFolder;
                StatusMessage = $"Scanning folder {p.FoldersScanned}: {p.CurrentFolder}";
            });

            var results = await _service.AnalyzeAsync(SelectedPath, progress, ct);

            foreach (var e in results)
                Entries.Add(e);

            EntryCount = Entries.Count;
            TotalSize = Entries.Sum(e => e.SizeBytes);
            TotalFiles = Entries.Sum(e => e.FileCount);
            TotalFolders = Entries.Sum(e => e.FolderCount);

            ScanSummary = EntryCount == 0
                ? "No subfolders found."
                : $"{EntryCount} folders · {FormatSize(TotalSize)} total · {TotalFiles:N0} files";
            StatusMessage = "Analysis complete.";
            Log.Information("Disk analysis completed: {Folders} folders, {Size} total",
                EntryCount, FormatSize(TotalSize));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            CurrentFolder = "";
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _cts?.Cancel();

    [RelayCommand]
    private static void ShowInExplorer(DiskUsageEntry? entry)
    {
        if (entry == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{entry.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void DrillDown(DiskUsageEntry? entry)
    {
        if (entry == null || entry.Name == "(files in root)") return;
        SelectedPath = entry.FullPath;
        if (!PresetPaths.Contains(entry.FullPath))
            PresetPaths.Add(entry.FullPath);
        _ = AnalyzeAsync();
    }

    [RelayCommand]
    private void GoUp()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;
        var parent = Directory.GetParent(SelectedPath);
        if (parent != null)
        {
            SelectedPath = parent.FullName;
            _ = AnalyzeAsync();
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to analyze"
        };
        if (dialog.ShowDialog() == true)
        {
            SelectedPath = dialog.FolderName;
            if (!PresetPaths.Contains(SelectedPath))
                PresetPaths.Add(SelectedPath);
        }
    }

    private void UpdateDriveInfo()
    {
        try
        {
            var root = Path.GetPathRoot(SelectedPath);
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady)
                {
                    DriveTotal = di.TotalSize;
                    DriveFree = di.AvailableFreeSpace;
                    DriveUsed = DriveTotal - DriveFree;
                    DriveUsedPercent = DriveTotal > 0
                        ? Math.Round(DriveUsed * 100.0 / DriveTotal, 1)
                        : 0;
                    HasDriveInfo = true;
                    return;
                }
            }
        }
        catch { }
        HasDriveInfo = false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
