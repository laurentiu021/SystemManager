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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelScanCommand : null;

    private readonly DuplicateFileService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<DuplicateFileGroup> Groups { get; } = new();
    public ObservableCollection<string> PresetFolders { get; } = new();

    [ObservableProperty] private string _selectedFolder = "";

    /// <summary>
    /// Minimum file size to consider, in KB, typed straight into a TextBox by the user.
    /// <para>Deliberately NOT clamped in the setter: the box uses
    /// <c>UpdateSourceTrigger=PropertyChanged</c>, so rewriting the value on every keystroke would
    /// fight the caret (typing "-" then a digit would snap the field out from under them). The bound
    /// is applied where the value is USED — see <see cref="MinBytesFor"/>.</para>
    /// </summary>
    [ObservableProperty] private long _minSizeKb = 1;
    [ObservableProperty] private long _totalWasted;
    [ObservableProperty] private int _groupCount;
    [ObservableProperty] private int _duplicateFileCount;
    [ObservableProperty] private string _scanSummary = "Select a folder and click Scan.";
    [ObservableProperty] private string _currentFile = "";

    /// <summary>
    /// The fast half of the scan report — running counts and the file being read — kept OUT of
    /// <see cref="ViewModelBase.StatusMessage"/> so it is shown without being announced.
    /// <para>The status line is a live region, and it used to carry this. The service throttles its reports
    /// to one every 200 ms, so the announced line changed about five times a second for the length of the
    /// scan: a screen reader started a new sentence before finishing the last one and conveyed less than a
    /// line every few seconds would (#2143). Splitting the two is the shape Deep Cleanup already uses —
    /// announce the coarsest line the tab has, leave the fine readouts silent — and this tab was the only
    /// exception left.</para>
    /// </summary>
    [ObservableProperty] private string _scanReadout = "";

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
        StatusMessage = ScanningLabel;
        Groups.Clear();
        TotalWasted = 0;
        GroupCount = 0;
        DuplicateFileCount = 0;

        try
        {
            var minBytes = MinBytesFor(MinSizeKb);
            var progress = new Progress<DuplicateFileService.ScanProgress>(ApplyScanProgress);

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
            // The counts belong to a scan that is over; the summary bar carries the totals from here.
            // Leaving them would also leave the last file name sitting beside "Scan complete."
            ScanReadout = "";
        }
    }

    /// <summary>The status line while a scan is starting, and the fallback if a report carries no phase.</summary>
    private const string ScanningLabel = "Scanning…";

    /// <summary>
    /// Applies one progress report: the phase to the announced status line, the counts and current file to
    /// the silent <see cref="ScanReadout"/>.
    /// <para>The phase is assigned on every report, five times a second, and that is deliberate rather than
    /// wasteful: the generated setter drops a value equal to the current one, so PropertyChanged — and with
    /// it the announcement — fires only when the phase actually changes. That is twice per scan whatever the
    /// folder contains, which is the pace a spoken line can keep up with. A rounded count would not have
    /// that property: any fixed rounding step announces more often the bigger the folder is.</para>
    /// <para>Internal so a test can drive the real callback body with a synthetic stream of reports; the
    /// split only holds if the announced property is assigned the coarse half, and nothing else can see
    /// that.</para>
    /// </summary>
    internal void ApplyScanProgress(DuplicateFileService.ScanProgress p)
    {
        // The final report exists so a consumer sees settled counts, and its CurrentFile is the placeholder
        // "Done" rather than a file. The lines after the await say the same thing in a full sentence, so
        // rendering it here would both show a file that does not exist and announce completion twice.
        if (string.Equals(p.Phase, DuplicateFileService.CompletePhase, StringComparison.Ordinal)) return;

        CurrentFile = p.CurrentFile;
        StatusMessage = string.IsNullOrWhiteSpace(p.Phase) ? ScanningLabel : p.Phase;
        ScanReadout = BuildScanReadout(p);
    }

    /// <summary>The largest <see cref="MinSizeKb"/> that still scales into a <c>long</c> byte count.</summary>
    internal const long MaxMinSizeKb = long.MaxValue / 1024;

    /// <summary>
    /// Converts the user's KB figure into the byte threshold the scan compares against, bounded so a
    /// typed value can never INVERT the filter.
    /// <para>The scan skips a file with <c>if (fi.Length &lt; minSizeBytes) continue;</c>. A negative
    /// threshold makes that comparison false for EVERY file, so "only files above X" silently becomes
    /// "every file in the folder" — the opposite of the request, and a slow one, because every file then
    /// gets hashed. Two typed values reach it: a stray leading minus, and any figure above
    /// <see cref="MaxMinSizeKb"/>, which overflows <c>long</c> when multiplied by 1024 and lands
    /// negative. The TextBox binds a <c>long</c> and imposes no bounds of its own, so both are
    /// reachable.</para>
    /// <para>Pure and static so the bound is testable without touching a filesystem.</para>
    /// </summary>
    internal static long MinBytesFor(long minSizeKb) => Math.Clamp(minSizeKb, 0, MaxMinSizeKb) * 1024;

    /// <summary>
    /// The silent half of the scan report: running counts and the file currently being read.
    /// <para>The file name was reported by the service and assigned to <see cref="CurrentFile"/> on every
    /// progress tick, but nothing displayed it — so a scan of a large folder showed only rising numbers,
    /// with no sign of which file it was on or whether it had stalled on one.</para>
    /// <para>Only the leaf is shown: the service reports the full path, and a deep one would dominate the
    /// row. The whole path is on the row's tooltip, so nothing is lost — that pairing is the point, and it
    /// did not hold until the service stopped reporting a bare name (#2262).</para>
    /// <para>The phase is NOT repeated here: it is what the announced status line beside this one carries,
    /// so printing it twice would put the same word on the row twice (#2143).</para>
    /// <para>Pure and static so the formatting is testable without running a scan.</para>
    /// </summary>
    internal static string BuildScanReadout(DuplicateFileService.ScanProgress p)
    {
        var counts = string.Create(CultureInfo.InvariantCulture, $"{p.FilesDiscovered:N0} found, {p.FilesHashed:N0} hashed");

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
                FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"),
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
