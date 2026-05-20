// SysManager · DeepCleanupViewModel — opt-in cleanup + read-only large files
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
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
    private readonly EtaCalculator _scanEta = new();
    private readonly EtaCalculator _cleanEta = new();

    public BulkObservableCollection<CleanupCategory> Categories { get; } = new();
    public BulkObservableCollection<LargeFileEntry> LargeFiles { get; } = new();
    public ObservableCollection<ScanLocation> ScanLocations { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private bool _isLargeScanning;

    // Scan progress (determinate, category-based)
    [ObservableProperty] private int _scanProgress;          // 0..100
    [ObservableProperty] private string _scanStatusLine = string.Empty;
    [ObservableProperty] private string _scanEtaText = string.Empty;
    [ObservableProperty] private int _cleanProgress;         // 0..100
    [ObservableProperty] private string _cleanStatusLine = string.Empty;
    [ObservableProperty] private string _cleanEtaText = string.Empty;

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
    public string TotalSelectedDisplay => FormatHelper.FormatSize(TotalSelectedBytes);

    public string LargeBytesScannedDisplay => FormatHelper.FormatSize(LargeBytesScanned);

    public DeepCleanupViewModel()
    {
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        try { await LoadLocationsAsync(); }
        catch (IOException ex) { Log.Warning("Deep cleanup location load failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Deep cleanup location load failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Deep cleanup location load failed: {Error}", ex.Message); }
    }

    private async Task LoadLocationsAsync()
    {
        try
        {
            ScanLocations.Clear();

            AddLocation("📥  Downloads", Helpers.KnownFolders.GetDownloadsPath());
            AddLocation("📄  Documents", Helpers.KnownFolders.GetDocumentsPath());
            AddLocation("🖥️  Desktop", Helpers.KnownFolders.GetDesktopPath());
            AddLocation("🎬  Videos", Helpers.KnownFolders.GetVideosPath());
            AddLocation("🖼️  Pictures", Helpers.KnownFolders.GetPicturesPath());
            AddLocation("🎵  Music", Helpers.KnownFolders.GetMusicPath());

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddLocation("💼  Program Files", pf);
            AddLocation("💼  Program Files (x86)", pfx86);

            var drives = await _drives.EnumerateAsync();
            foreach (var d in drives)
                AddLocation($"💾  Whole drive  {d.Letter}  ({d.SizeGB:F0} GB)", d.Letter + @"\");

            SelectedLocation = ScanLocations.FirstOrDefault();
        }
        catch (IOException) { /* location enumeration is best-effort */ }
        catch (UnauthorizedAccessException) { /* location enumeration is best-effort */ }
    }

    private void AddLocation(string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        ScanLocations.Add(new ScanLocation(label, path));
    }

    partial void OnLargeBytesScannedChanged(long value) => OnPropertyChanged(nameof(LargeBytesScannedDisplay));

    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsScanningChanged(bool value) => IsBusy = IsScanning || IsCleaning || IsLargeScanning;
    partial void OnIsCleaningChanged(bool value) => IsBusy = IsScanning || IsCleaning || IsLargeScanning;
    partial void OnIsLargeScanningChanged(bool value) => IsBusy = IsScanning || IsCleaning || IsLargeScanning;

    private void OnCategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategory.IsSelected))
        {
            OnPropertyChanged(nameof(TotalSelectedBytes));
            OnPropertyChanged(nameof(TotalSelectedDisplay));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var c in Categories)
                c.PropertyChanged -= OnCategoryPropertyChanged;
            _scanCts?.Dispose();
            _cleanCts?.Dispose();
            _largeCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- deep cleanup scan ----------

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup Scan");
        if (opLock == null)
        {
            ScanSummary = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }
        await ScanCoreAsync();
    }

    /// <summary>
    /// Inner scan logic without lock acquisition. Called directly from CleanAsync
    /// (which already holds the disk operation lock) to avoid deadlock.
    /// </summary>
    private async Task ScanCoreAsync()
    {
        IsScanning = true;
        ScanProgress = 0;
        ScanStatusLine = "Starting...";
        ScanEtaText = "";
        _scanEta.Reset();
        ScanSummary = "Scanning safe cleanup locations...";
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DeepCleanupService.ScanProgress>(p =>
            {
                ScanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                ScanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
                ScanEtaText = _scanEta.Update(ScanProgress);
            });
            var cats = await _cleanup.ScanAsync(progress, _scanCts.Token);

            // MEM-006: Unsubscribe from old categories before clearing to prevent
            // PropertyChanged lambda leaks across rescans.
            foreach (var old in Categories)
                old.PropertyChanged -= OnCategoryPropertyChanged;
            var catList = cats.ToList();
            foreach (var c in catList)
                c.PropertyChanged += OnCategoryPropertyChanged;
            Categories.ReplaceWith(catList);
            var total = cats.Sum(c => c.TotalSizeBytes);
            ScanSummary = $"Found {FormatHelper.FormatSize(total)} across {cats.Count} categories. Untick anything you want to keep.";
            ScanStatusLine = "Scan complete.";
            Log.Information("Deep cleanup scan completed: {Size} across {Count} categories",
                FormatHelper.FormatSize(total), cats.Count);
            OnPropertyChanged(nameof(TotalSelectedBytes));
            OnPropertyChanged(nameof(TotalSelectedDisplay));
        }
        catch (OperationCanceledException) { ScanSummary = "Scan cancelled."; ScanStatusLine = "Cancelled."; }
        catch (IOException ex) { ScanSummary = $"Scan failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ScanSummary = $"Scan failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsCleaning || !Categories.Any(c => c.IsSelected)) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        if (opLock == null)
        {
            CleanSummary = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }
        IsCleaning = true;
        CleanProgress = 0;
        CleanStatusLine = "Starting...";
        CleanEtaText = "";
        _cleanEta.Reset();
        CleanSummary = "Cleaning selected categories — you can keep using the app.";
        _cleanCts?.Dispose();
        _cleanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DeepCleanupService.ScanProgress>(p =>
            {
                CleanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                CleanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
                CleanEtaText = _cleanEta.Update(CleanProgress);
            });
            var result = await _cleanup.CleanAsync(Categories, progress, _cleanCts.Token);
            CleanSummary = result.Summary;
            CleanStatusLine = "Clean complete.";
            Log.Information("Deep cleanup completed");
            await ScanCoreAsync();
        }
        catch (OperationCanceledException) { CleanSummary = "Clean cancelled."; CleanStatusLine = "Cancelled."; }
        catch (IOException ex) { CleanSummary = $"Clean failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { CleanSummary = $"Clean failed: {ex.Message}"; }
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
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Large File Scan");
        if (opLock == null)
        {
            LargeScanStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }
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
        _largeCts?.Dispose();
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
            LargeFiles.ReplaceWith(list);
            LargeScanStatus = $"Found {list.Count} files ≥ {MinSizeMB} MB in {SelectedLocation.Label.Trim()}.";
            Log.Information("Large file scan completed: {Count} files ≥ {MinSize} MB",
                list.Count, MinSizeMB);
        }
        catch (OperationCanceledException) { LargeScanStatus = "Scan cancelled."; }
        catch (IOException ex) { LargeScanStatus = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { LargeScanStatus = $"Error: {ex.Message}"; }
        finally { IsLargeScanning = false; LargeCurrentFolder = string.Empty; }
    }

    [RelayCommand]
    private void ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (InvalidOperationException) { /* best-effort */ }
        catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { System.Windows.Clipboard.SetText(path); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard may be locked */ }
    }
}

/// <summary>Labelled location the user can pick in the large-files finder.</summary>
public sealed record ScanLocation(string Label, string Path);
