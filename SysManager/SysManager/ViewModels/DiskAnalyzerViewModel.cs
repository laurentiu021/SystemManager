// SysManager · DiskAnalyzerViewModel — disk space breakdown
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
/// Disk Analyzer tab — shows space breakdown by top-level folders.
/// Read-only: only "Show in Explorer" is offered.
/// </summary>
public sealed partial class DiskAnalyzerViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelAnalysisCommand : null;

    private readonly DiskAnalyzerService _service;
    private readonly DiskScanHistoryService _history;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<DiskUsageEntry> Entries { get; } = new();
    public ObservableCollection<string> PresetPaths { get; } = new();

    [ObservableProperty] private string _selectedPath = "";
    [ObservableProperty] private string _scanSummary = "Select a drive or folder and click Analyze.";

    // The delta line: "3.2 GB larger than your last scan on 12 Jul", or empty when this root has no
    // remembered scan yet. Explicitly "since last scan", never framed as continuous monitoring — the
    // sampling is user-triggered and irregular. Empty string keeps the row collapsed (see the view).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrend))]
    private string _trendSummary = "";

    public bool HasTrend => !string.IsNullOrEmpty(TrendSummary);
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _entryCount;

    // Distinguishes the un-run state from a completed zero-result scan so the big empty-state overlay
    // doesn't tell the user to "pick a folder and analyze" right after they did exactly that. Set true
    // only after a scan actually completes (see AnalyzeAsync); a cancelled/failed scan leaves it as-is.
    // Mirrors DuplicateFileViewModel, the sibling tab in the same Storage group.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private bool _hasScanned;

    public string EmptyTitle => HasScanned ? "Nothing to show" : "No results yet";
    public string EmptyMessage => HasScanned
        ? "This folder has no subfolders using measurable space."
        : "Pick a folder and analyze to see what's using space.";

    // Drive-level info
    [ObservableProperty] private long _driveTotal;
    [ObservableProperty] private long _driveFree;
    [ObservableProperty] private long _driveUsed;
    [ObservableProperty] private double _driveUsedPercent;
    [ObservableProperty] private bool _hasDriveInfo;

    /// <summary>
    /// Says that the total is partial by design. Without this the user compares it against the free
    /// space Windows reports, finds a multi-gigabyte gap — <c>Windows\WinSxS</c> alone is routinely
    /// several GB — and has no way to learn the difference is intentional.
    /// </summary>
    /// <remarks>
    /// Instance, not static, even though the value never varies: a <c>{Binding}</c> to a static member
    /// resolves to nothing and renders EMPTY, which would reintroduce the very silence this fixes.
    /// (Nothing in Views/ uses <c>x:Static</c>, so an instance property is also the uniform choice.)
    /// </remarks>
    public string ExclusionNote =>
        "Windows system areas and shortcut-links (junctions) aren't counted, so this total can be " +
        "smaller than the space Windows reports.";

    /// <summary>The exact excluded folders, for the curious — derived from the service's own list.</summary>
    public string ExclusionDetail =>
        "Not counted: " + string.Join(", ", DiskAnalyzerService.ExcludedFolderNames) +
        ", plus any junction or symbolic link (following one would double-count, or lead outside the " +
        "folder you asked about).";

    public DiskAnalyzerViewModel(DiskAnalyzerService service, DiskScanHistoryService history)
    {
        _service = service;
        _history = history;
        // Probe drives off the UI thread: DriveInfo.IsReady can stall on a disconnected
        // mapped/removable volume. This tab is LAZY — NavItem.ContentFactory builds it on first open,
        // not at startup (the eager set is Dashboard, DarkMode and About; see the list above
        // NavItems) — so the stall this avoids is on the first navigation into the tab, not at app
        // launch. The Task.Run is still required either way; the collection update runs back on the
        // UI thread.
        InitializeAsync(PopulatePresetsAsync);
    }

    private async Task PopulatePresetsAsync()
    {
        var paths = await Task.Run(EnumeratePresetPaths).ConfigureAwait(true);
        foreach (var p in paths)
            PresetPaths.Add(p);
        if (PresetPaths.Count > 0)
            SelectedPath = PresetPaths[0];
    }

    private static List<string> EnumeratePresetPaths()
    {
        var result = new List<string>();
        foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            result.Add(d.RootDirectory.FullName);

        string[] special =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        foreach (var p in special.Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x) && !result.Contains(x)))
            result.Add(p);

        return result;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;

        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Disk Analysis");
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
        StatusMessage = "Analyzing…";
        TrendSummary = "";
        Entries.Clear();
        TotalSize = 0;
        TotalFiles = 0;
        EntryCount = 0;

        UpdateDriveInfo();

        try
        {
            var progress = new Progress<DiskAnalyzerService.AnalysisProgress>(p =>
            {
                StatusMessage = $"Scanning folder {p.FoldersScanned}: {p.CurrentFolder}";
            });

            var results = await _service.AnalyzeAsync(SelectedPath, progress, ct);

            Entries.ReplaceWith(results);

            EntryCount = Entries.Count;
            TotalSize = Entries.Sum(e => e.SizeBytes);
            TotalFiles = Entries.Sum(e => e.FileCount);

            ScanSummary = EntryCount == 0
                ? "No subfolders found."
                : string.Create(CultureInfo.InvariantCulture, $"{EntryCount} folders · {FormatSize(TotalSize)} total · {TotalFiles:N0} files");
            HasScanned = true;
            await UpdateTrendAndRememberAsync(SelectedPath, ct).ConfigureAwait(true);
            StatusMessage = "Analysis complete.";
            ToastService.Instance.Show("Disk Analysis complete", $"{EntryCount} folders, {FormatSize(TotalSize)} total");
            Log.Information("Disk analysis completed: {Folders} folders, {Size} total",
                EntryCount, FormatSize(TotalSize));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
        }
        catch (IOException ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// Reads the remembered scan of <paramref name="root"/> to show what changed, then remembers this
    /// scan in its place. Deliberately swallows its own failures: the scan has already completed and its
    /// results are on screen, so a history read/write problem must degrade to "no trend line", never to a
    /// broken tab. That is the same never-throw-to-the-caller contract the history service keeps
    /// internally; this is the second half of it, at the call site.
    /// </summary>
    private async Task UpdateTrendAndRememberAsync(string root, CancellationToken ct)
    {
        try
        {
            var previous = await _history.FindAsync(root, ct).ConfigureAwait(true);
            TrendSummary = DescribeTrend(previous, TotalSize);

            var snapshot = new DiskScanSnapshot
            {
                RootPath = root,
                CapturedAt = DateTime.Now,
                TotalSize = TotalSize,
                TopFolders = Entries
                    .Select(e => new FolderUsage { Name = e.Name, SizeBytes = e.SizeBytes })
                    .ToList(),
            };
            await _history.SaveAsync(snapshot, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The tab closed between the scan finishing and this running — nothing to record.
        }
    }

    /// <summary>
    /// The one-line "since last scan" delta. Returns empty when this root has never been scanned, so a
    /// first-ever scan shows no trend rather than a misleading "0 bytes larger". A change smaller than a
    /// tenth of a percent reads as "about the same" rather than a spurious few-kilobyte delta.
    /// </summary>
    internal static string DescribeTrend(DiskScanSnapshot? previous, long currentTotal)
    {
        if (previous is null) return "";

        var on = previous.CapturedAt.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        var delta = currentTotal - previous.TotalSize;
        var magnitude = Math.Abs(delta);

        // Below 0.1% of the previous total (and at least a token 1 MB) counts as unchanged, so ordinary
        // churn does not read as growth.
        var threshold = Math.Max(1L * 1024 * 1024, previous.TotalSize / 1000);
        if (magnitude < threshold)
            return $"About the same as your last scan on {on}.";

        var direction = delta > 0 ? "larger" : "smaller";
        return $"{FormatSize(magnitude)} {direction} than your last scan on {on}.";
    }

    [RelayCommand]
    private void CancelAnalysis() => _cts?.Cancel();

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
    private static void ShowInExplorer(DiskUsageEntry? entry)
    {
        if (entry is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"),
                Arguments = $"\"{entry.FullPath}\"",
                UseShellExecute = true
            })?.Dispose();
        }
        catch (InvalidOperationException ex) { Log.Debug(ex, "Failed to open explorer for {Path}", entry.FullPath); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug(ex, "Failed to open explorer for {Path}", entry.FullPath); }
    }

    [RelayCommand]
    private async Task DrillDown(DiskUsageEntry? entry)
    {
        if (entry is null || entry.Name == "(files in root)") return;
        SelectedPath = entry.FullPath;
        if (!PresetPaths.Contains(entry.FullPath))
            PresetPaths.Add(entry.FullPath);
        await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task GoUp()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;
        var parent = Directory.GetParent(SelectedPath);
        if (parent is not null)
        {
            SelectedPath = parent.FullName;
            await AnalyzeAsync();
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
        catch (IOException ex) { Log.Debug(ex, "Failed to read drive info for {Path}", SelectedPath); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Access denied reading drive info for {Path}", SelectedPath); }
        HasDriveInfo = false;
    }

    private static string FormatSize(long bytes) => Helpers.FormatHelper.FormatSize(bytes);
}
