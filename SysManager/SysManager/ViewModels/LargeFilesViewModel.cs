// SysManager · LargeFilesViewModel
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

/// <summary>
/// ViewModel for the Large Files tab: shows the biggest files in a chosen location so the user can see
/// where their disk space went. Strictly read-only — the results offer "Show in Explorer" and "Copy path"
/// and nothing else, deliberately, even with administrator rights.
/// </summary>
/// <remarks>
/// Extracted from <see cref="DeepCleanupViewModel"/> in #1523, and it belongs in the Storage group for one
/// reason: all three tabs there — Disk Analyzer, this, Duplicate Finder — answer the same question, "where
/// did my disk space go?", and none of them delete anything. Sitting below a scan-and-delete UI on the
/// Cleanup tab, this half was both hard to find and easy to mistake for part of the deletion.
/// <para>The split was clean because the two halves shared nothing: the locations list, the drive
/// enumeration and the size threshold were only ever read by the large-file scan, so Deep Cleanup lost two
/// dependencies (<see cref="LargeFileScanner"/> and <see cref="FixedDriveService"/>) rather than gaining a
/// shared seam.</para>
/// </remarks>
public sealed partial class LargeFilesViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsScanning ? CancelCommand : null;

    private readonly LargeFileScanner _scanner;
    private readonly FixedDriveService _drives;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<LargeFileEntry> Files { get; } = new();
    public ObservableCollection<ScanLocation> ScanLocations { get; } = new();

    [ObservableProperty] private bool _isScanning;

    // Progress is indeterminate and counter-based: the scanner cannot know how many files a location
    // holds until it has walked it, so there is no percentage to report — only how far it has got.
    [ObservableProperty] private long _filesScanned;
    [ObservableProperty] private long _bytesScanned;
    [ObservableProperty] private string _currentFolder = string.Empty;

    [ObservableProperty] private string _scanStatus = string.Empty;
    [ObservableProperty] private int _minSizeMB = 500;
    [ObservableProperty] private ScanLocation? _selectedLocation;
    [ObservableProperty] private int _topCount = 100;

    public string BytesScannedDisplay => FormatHelper.FormatSize(BytesScanned);

    public LargeFilesViewModel(LargeFileScanner scanner, FixedDriveService drives)
    {
        _scanner = scanner;
        _drives = drives;
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        try { await LoadLocationsAsync(); }
        catch (IOException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
    }

    private async Task LoadLocationsAsync()
    {
        try
        {
            ScanLocations.Clear();

            AddLocation("📥  Downloads", KnownFolders.GetDownloadsPath());
            AddLocation("📄  Documents", KnownFolders.GetDocumentsPath());
            AddLocation("🖥️  Desktop", KnownFolders.GetDesktopPath());
            AddLocation("🎬  Videos", KnownFolders.GetVideosPath());
            AddLocation("🖼️  Pictures", KnownFolders.GetPicturesPath());
            AddLocation("🎵  Music", KnownFolders.GetMusicPath());

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
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        ScanLocations.Add(new ScanLocation(label, path));
    }

    partial void OnBytesScannedChanged(long value) => OnPropertyChanged(nameof(BytesScannedDisplay));

    // Forward running state to IsBusy so the sidebar progress indicator works, and re-evaluate Cancel.
    partial void OnIsScanningChanged(bool value)
    {
        IsBusy = value;
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Large File Scan");
        if (opLock is null)
        {
            ScanStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }
        if (SelectedLocation is null)
        {
            ScanStatus = "Pick a location first.";
            return;
        }

        IsScanning = true;
        Files.Clear();
        FilesScanned = 0;
        BytesScanned = 0;
        CurrentFolder = string.Empty;
        ScanStatus = $"Scanning {SelectedLocation.Label.Trim()}...";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<LargeFileScanner.LargeFileProgress>(p =>
            {
                FilesScanned = p.FilesScanned;
                BytesScanned = p.BytesScanned;
                CurrentFolder = p.CurrentFolder;
            });
            var list = await _scanner.ScanAsync(
                rootPath: SelectedLocation.Path,
                minSizeBytes: (long)MinSizeMB * 1024L * 1024L,
                top: TopCount,
                progress: progress,
                ct: _cts.Token);
            Files.ReplaceWith(list);
            ScanStatus = $"Found {list.Count} files ≥ {MinSizeMB} MB in {SelectedLocation.Label.Trim()}.";
            ToastService.Instance.Show("Large file scan complete", $"{list.Count} files found ≥ {MinSizeMB} MB");
            Log.Information("Large file scan completed: {Count} files ≥ {MinSize} MB", list.Count, MinSizeMB);
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled."; }
        catch (IOException ex) { ScanStatus = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ScanStatus = $"Error: {ex.Message}"; }
        finally { IsScanning = false; CurrentFolder = string.Empty; }
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(SystemPaths.ResolveSystemTool("explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose(); }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Labelled location the user can pick in the large-files finder.</summary>
public sealed record ScanLocation(string Label, string Path);
