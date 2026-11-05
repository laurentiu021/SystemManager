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

    private readonly Func<string, long, int, IProgress<LargeFileScanner.LargeFileProgress>?, CancellationToken,
        Task<IReadOnlyList<LargeFileEntry>>> _scan;
    private readonly Func<Task<IReadOnlyList<FixedDriveService.FixedDrive>>> _enumerateDrives;
    private readonly Action<string> _selectInExplorer;
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

    /// <summary>
    /// True while the tab is on screen. Set by <see cref="MainWindowViewModel.SetActive"/>.
    /// </summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>The read of the locations the tab started when it was last shown. Internal so a test can await it.</summary>
    internal Task ShownLocations { get; private set; } = Task.CompletedTask;

    public string BytesScannedDisplay => FormatHelper.FormatSize(BytesScanned);

    public LargeFilesViewModel(LargeFileScanner scanner, FixedDriveService drives)
        : this(scanner, drives, selectInExplorer: null)
    {
    }

    /// <summary>Builds the tab with its own way of showing a file in File Explorer.</summary>
    /// <param name="selectInExplorer">
    /// Opens File Explorer with a file selected: the real launch unless a test passes its own, so a test of the checks
    /// in front of it cannot open a window on the PC running the suite when one of them breaks (#2740).
    /// </param>
    /// <param name="scan">
    /// The scan: <paramref name="scanner"/>'s unless a test passes its own, to hold it while the selection changes
    /// (#2764).
    /// </param>
    /// <param name="enumerateDrives">
    /// Lists the fixed drives: <paramref name="drives"/>' list unless a test passes its own, to connect a drive (#2764).
    /// </param>
    internal LargeFilesViewModel(LargeFileScanner scanner, FixedDriveService drives, Action<string>? selectInExplorer,
        Func<string, long, int, IProgress<LargeFileScanner.LargeFileProgress>?, CancellationToken,
            Task<IReadOnlyList<LargeFileEntry>>>? scan = null,
        Func<Task<IReadOnlyList<FixedDriveService.FixedDrive>>>? enumerateDrives = null)
    {
        _scan = scan ?? scanner.ScanAsync;
        _enumerateDrives = enumerateDrives ?? (() => drives.EnumerateAsync());
        _selectInExplorer = selectInExplorer ?? SelectInExplorer;
        InitializeAsync(InitAsync);
    }

    private static void SelectInExplorer(string path) =>
        Process.Start(new ProcessStartInfo(SystemPaths.ResolveSystemTool("explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();

    private async Task InitAsync()
    {
        try { await LoadLocationsAsync(); }
        catch (IOException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Large-files location load failed: {Error}", ex.Message); }
    }

    /// <summary>Reads the locations again, keeping the one picked while it is still listed.</summary>
    private async Task LoadLocationsAsync()
    {
        var picked = SelectedLocation?.Path;
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

            var drives = await _enumerateDrives();
            foreach (var d in drives)
                AddLocation($"💾  Whole drive  {d.Letter}  ({d.SizeGB:F0} GB)", d.Letter + @"\");

            SelectedLocation = ScanLocations.FirstOrDefault(l => string.Equals(l.Path, picked, StringComparison.OrdinalIgnoreCase))
                ?? ScanLocations.FirstOrDefault();
        }
        catch (IOException) { /* location enumeration is best-effort */ }
        catch (UnauthorizedAccessException) { /* location enumeration is best-effort */ }
    }

    private void AddLocation(string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        ScanLocations.Add(new ScanLocation(label, path));
    }

    /// <summary>Shown again: reads the locations again, keeping the one picked.</summary>
    /// <remarks>
    /// The tab is built once and kept for the whole run, so the list read when it was built went stale: a drive connected
    /// later never appeared, and one unplugged stayed listed with its old size (#2764). Not while the first read or a scan
    /// runs.
    /// </remarks>
    partial void OnIsActiveChanged(bool value)
    {
        if (value && InitializationComplete.IsCompleted && !IsScanning) ShownLocations = InitAsync();
    }

    partial void OnBytesScannedChanged(long value) => OnPropertyChanged(nameof(BytesScannedDisplay));

    // Forward running state to IsBusy so the sidebar progress indicator works, and re-evaluate Cancel. The tab's bar
    // is a marquee with no figure of its own, so the taskbar button is told the same; with IsBusy alone it stayed blank
    // for the whole of a scan that can take minutes (#2872).
    partial void OnIsScanningChanged(bool value)
    {
        IsBusy = value;
        IsProgressIndeterminate = value;
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
        // Read once: the box and the size stay open while the scan runs, and the line, the toast and the log describe
        // what was read. They were built from the selection again once the scan had ended (#2764).
        var location = SelectedLocation;
        var minSizeMb = MinSizeMB;
        var top = TopCount;
        if (location is null)
        {
            ScanStatus = "Pick a location first.";
            return;
        }

        // A drive unplugged since the list was read, or a folder since removed: the scanner returns nothing for either,
        // and that read as a scan that found nothing (#2764).
        if (!Directory.Exists(location.Path))
        {
            ScanStatus = string.Equals(Path.GetPathRoot(location.Path), location.Path, StringComparison.OrdinalIgnoreCase)
                ? $"Drive {location.Path.TrimEnd('\\')} is no longer connected, so nothing was scanned."
                : $"{location.Label.Trim()} is no longer there, so nothing was scanned.";
            await InitAsync();
            return;
        }

        IsScanning = true;
        Files.Clear();
        FilesScanned = 0;
        BytesScanned = 0;
        CurrentFolder = string.Empty;
        ScanStatus = $"Scanning {location.Label.Trim()}...";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new SettlingProgress<LargeFileScanner.LargeFileProgress>(p =>
            {
                FilesScanned = p.FilesScanned;
                BytesScanned = p.BytesScanned;
                CurrentFolder = p.CurrentFolder;
            });
            var list = await progress.SettleAfterAsync(reporter => _scan(
                location.Path,
                (long)minSizeMb * 1024L * 1024L,
                top,
                reporter,
                _cts.Token));
            Files.ReplaceWith(list);
            ScanStatus = $"Found {list.Count} files ≥ {minSizeMb} MB in {location.Label.Trim()}.";
            ToastService.Instance.Show("Large file scan complete", $"{list.Count} files found ≥ {minSizeMb} MB");
            Log.Information("Large file scan completed: {Count} files ≥ {MinSize} MB", list.Count, minSizeMb);
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
        if (string.IsNullOrWhiteSpace(path)) return;
        // ScanStatus, not StatusMessage: this tab's one status line is bound to ScanStatus
        // (LargeFilesView.xaml), so a write to the base class's own property would be silently invisible.
        // The scan is a point in time; the user acts on the row afterwards, and the file can be gone by
        // then — silently doing nothing left no way to tell "nothing happened" from "it opened behind
        // another window" (#2859).
        if (!File.Exists(path))
        {
            ScanStatus = "File not found — it may have been moved or deleted since the scan.";
            return;
        }
        try
        {
            _selectInExplorer(path);
        }
        catch (InvalidOperationException) { ScanStatus = "Could not open file location."; }
        catch (System.ComponentModel.Win32Exception) { ScanStatus = "Could not open file location."; }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ScanStatus = ClipboardFeedback.TrySetTextAndDescribe(path);
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
public sealed record ScanLocation(string Label, string Path)
{
    /// <summary>The label, which a screen reader says for the location in the picker (#2785).</summary>
    public override string ToString() => Label;
}
