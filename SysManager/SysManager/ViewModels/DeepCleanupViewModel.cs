// SysManager · DeepCleanupViewModel — opt-in cleanup + read-only large files
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class DeepCleanupViewModel : ViewModelBase
{
    private readonly DeepCleanupService _cleanup = new();
    private readonly LargeFileScanner _largeFiles = new();
    private readonly FixedDriveService _drives = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _cleanCts;
    private CancellationTokenSource? _largeCts;

    public ObservableCollection<CleanupCategory> Categories { get; } = new();
    public ObservableCollection<LargeFileEntry> LargeFiles { get; } = new();
    public ObservableCollection<ScanLocation> ScanLocations { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private bool _isLargeScanning;

    // Scan progress (determinate, category-based)
    [ObservableProperty] private int _scanProgress;          // 0..100
    [ObservableProperty] private string _scanStatusLine = string.Empty;
    [ObservableProperty] private int _cleanProgress;         // 0..100
    [ObservableProperty] private string _cleanStatusLine = string.Empty;

    // Large files progress (indeterminate, counter-based)
    [ObservableProperty] private long _largeFilesScanned;
    [ObservableProperty] private long _largeBytesScanned;
    [ObservableProperty] private string _largeCurrentFolder = string.Empty;

    [ObservableProperty] private string _scanSummary = "Press 'Scan' to discover what can be safely freed.";
    [ObservableProperty] private string _cleanSummary = string.Empty;
    [ObservableProperty] private string _largeScanStatus = string.Empty;
    [ObservableProperty] private int _minSizeMB = 500;
    [ObservableProperty] private ScanLocation? _selectedLocation;
    [ObservableProperty] private int _topCount = 100;

    public long TotalSelectedBytes => Categories.Where(c => c.IsSelected).Sum(c => c.TotalSizeBytes);
    public string TotalSelectedDisplay => CleanupCategory.HumanSize(TotalSelectedBytes);

    public string LargeBytesScannedDisplay => CleanupCategory.HumanSize(LargeBytesScanned);

    public DeepCleanupViewModel()
    {
        _ = LoadLocationsAsync();
    }

    private async Task LoadLocationsAsync()
    {
        try
        {
            ScanLocations.Clear();

            AddLocation("📥  Downloads", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads");
            AddLocation("📄  Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddLocation("🖥️  Desktop",   Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddLocation("🎬  Videos",    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            AddLocation("🖼️  Pictures",  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddLocation("🎵  Music",     Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

            var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddLocation("💼  Program Files",      pf);
            AddLocation("💼  Program Files (x86)", pfx86);

            var drives = await _drives.EnumerateAsync();
            foreach (var d in drives)
                AddLocation($"💾  Whole drive  {d.Letter}  ({d.SizeGB:F0} GB)", d.Letter + @"\");

            SelectedLocation = ScanLocations.FirstOrDefault();
        }
        catch (Exception) { /* location enumeration is best-effort */ }
    }

    private void AddLocation(string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        ScanLocations.Add(new ScanLocation(label, path));
    }

    partial void OnLargeBytesScannedChanged(long value) => OnPropertyChanged(nameof(LargeBytesScannedDisplay));

    // ---------- deep cleanup scan ----------

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanProgress = 0;
        ScanStatusLine = "Starting...";
        ScanSummary = "Scanning safe cleanup locations...";
        _scanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DeepCleanupService.ScanProgress>(p =>
            {
                ScanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                ScanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
            });
            var cats = await _cleanup.ScanAsync(progress, _scanCts.Token);
            Categories.Clear();
            foreach (var c in cats)
            {
                c.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CleanupCategory.IsSelected))
                    {
                        OnPropertyChanged(nameof(TotalSelectedBytes));
                        OnPropertyChanged(nameof(TotalSelectedDisplay));
                    }
                };
                Categories.Add(c);
            }
            var total = cats.Sum(c => c.TotalSizeBytes);
            ScanSummary = $"Found {CleanupCategory.HumanSize(total)} across {cats.Count} categories. Untick anything you want to keep.";
            ScanStatusLine = "Scan complete.";
            Log.Information("Deep cleanup scan completed: {Size} across {Count} categories",
                CleanupCategory.HumanSize(total), cats.Count);
            OnPropertyChanged(nameof(TotalSelectedBytes));
            OnPropertyChanged(nameof(TotalSelectedDisplay));
        }
        catch (OperationCanceledException) { ScanSummary = "Scan cancelled."; ScanStatusLine = "Cancelled."; }
        catch (Exception ex) { ScanSummary = $"Scan failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsCleaning || !Categories.Any(c => c.IsSelected)) return;
        IsCleaning = true;
        CleanProgress = 0;
        CleanStatusLine = "Starting...";
        CleanSummary = "Cleaning selected categories — you can keep using the app.";
        _cleanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DeepCleanupService.ScanProgress>(p =>
            {
                CleanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                CleanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
            });
            var result = await _cleanup.CleanAsync(Categories, progress, _cleanCts.Token);
            CleanSummary = result.Summary;
            CleanStatusLine = "Clean complete.";
            Log.Information("Deep cleanup completed");
            await ScanAsync();
        }
        catch (OperationCanceledException) { CleanSummary = "Clean cancelled."; CleanStatusLine = "Cancelled."; }
        catch (Exception ex) { CleanSummary = $"Clean failed: {ex.Message}"; }
        finally { IsCleaning = false; }
    }

    [RelayCommand]
    private void SelectAll(bool? value)
    {
        var on = value ?? true;
        foreach (var c in Categories) c.IsSelected = on && !c.IsDestructiveHint;
    }

    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        _cleanCts?.Cancel();
        _largeCts?.Cancel();
    }

    // ---------- large files finder ----------

    [RelayCommand]
    private async Task ScanLargeFilesAsync()
    {
        if (IsLargeScanning) return;
        if (SelectedLocation == null)
        {
            LargeScanStatus = "Pick a location first.";
            return;
        }

        IsLargeScanning = true;
        LargeFiles.Clear();
        LargeFilesScanned = 0;
        LargeBytesScanned = 0;
        LargeCurrentFolder = string.Empty;
        LargeScanStatus = $"Scanning {SelectedLocation.Label.Trim()}...";
        _largeCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<LargeFileScanner.LargeFileProgress>(p =>
            {
                LargeFilesScanned = p.FilesScanned;
                LargeBytesScanned = p.BytesScanned;
                LargeCurrentFolder = p.CurrentFolder;
            });
            var list = await _largeFiles.ScanAsync(
                rootPath: SelectedLocation.Path,
                minSizeBytes: (long)MinSizeMB * 1024L * 1024L,
                top: TopCount,
                progress: progress,
                ct: _largeCts.Token);
            foreach (var f in list) LargeFiles.Add(f);
            LargeScanStatus = $"Found {list.Count} files ≥ {MinSizeMB} MB in {SelectedLocation.Label.Trim()}.";
            Log.Information("Large file scan completed: {Count} files ≥ {MinSize} MB",
                list.Count, MinSizeMB);
        }
        catch (OperationCanceledException) { LargeScanStatus = "Scan cancelled."; }
        catch (Exception ex) { LargeScanStatus = $"Error: {ex.Message}"; }
        finally { IsLargeScanning = false; LargeCurrentFolder = string.Empty; }
    }

    [RelayCommand]
    private void ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception) { /* best-effort */ }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { System.Windows.Clipboard.SetText(path); } catch (Exception) { /* clipboard may be locked */ }
    }
}

/// <summary>Labelled location the user can pick in the large-files finder.</summary>
public sealed record ScanLocation(string Label, string Path);
