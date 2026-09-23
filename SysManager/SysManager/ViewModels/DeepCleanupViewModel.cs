// SysManager · DeepCleanupViewModel — opt-in cleanup + read-only large files
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class DeepCleanupViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    private readonly DeepCleanupService _cleanup;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _cleanCts;
    private readonly EtaCalculator _scanEta = new();
    private readonly EtaCalculator _cleanEta = new();

    public BulkObservableCollection<CleanupCategory> Categories { get; } = new();

    /// <summary>Whether this session is elevated. Read by the shared <c>AdminBanner</c> from the DataContext.</summary>
    /// <remarks>
    /// Six of the buckets live under <c>%WinDir%</c> — the Windows Update download cache, the Delivery
    /// Optimization cache, the Installer patch cache, <c>Windows\Temp</c>, Prefetch and the blue-screen
    /// memory dumps — so an unelevated run cannot delete them. It did not fail visibly either: the files
    /// landed in <see cref="Models.CleanupCategory.SkippedCount"/> and the row read "N files · M skipped" without ever
    /// saying that administrator rights were the reason, which is the one thing the user could have acted on.
    /// </remarks>
    [ObservableProperty] private bool _isElevated;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;

    // Scan progress (determinate, category-based)
    [ObservableProperty] private int _scanProgress;          // 0..100
    [ObservableProperty] private string _scanStatusLine = string.Empty;
    [ObservableProperty] private string _scanEtaText = string.Empty;
    [ObservableProperty] private int _cleanProgress;         // 0..100
    [ObservableProperty] private string _cleanStatusLine = string.Empty;
    [ObservableProperty] private string _cleanEtaText = string.Empty;

    [ObservableProperty] private string _scanSummary = "Press 'Scan' to discover what can be safely freed.";
    [ObservableProperty] private string _cleanSummary = string.Empty;

    public long TotalSelectedBytes => Categories.Where(c => c.IsSelected).Sum(c => c.TotalSizeBytes);
    public string TotalSelectedDisplay => FormatHelper.FormatSize(TotalSelectedBytes);

    /// <summary>
    /// Clean is only valid when at least one category is ticked and no clean is
    /// already running. Gating CanExecute (rather than early-returning inside the
    /// command) keeps the button visibly disabled at 0 B selected, so an empty
    /// destructive action is never presented as clickable.
    /// </summary>
    private bool CanClean => !IsCleaning && Categories.Any(c => c.IsSelected);

    public DeepCleanupViewModel(DeepCleanupService cleanup)
    {
        _cleanup = cleanup;
        // Read synchronously rather than from an async init: the banner is above the fold and its two
        // states must not flicker from "needs administrator" to "running as administrator" after the page
        // has painted. Nothing else needs initialising here — the locations list this tab used to build
        // left with the large-files finder (#1523), so there is no InitializeAsync call any more.
        IsElevated = AdminHelper.IsElevated();
    }

    /// <summary>Restarts SysManager elevated, so the five <c>%WinDir%</c> buckets stop being skipped.</summary>
    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsScanningChanged(bool value) => IsBusy = IsScanning || IsCleaning;
    partial void OnIsCleaningChanged(bool value)
    {
        IsBusy = IsScanning || IsCleaning;
        CleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Copies the ticks the user set onto a freshly scanned set of categories, so a rescan does not throw
    /// their choices away.
    /// </summary>
    /// <remarks>
    /// Categories arrive from the service pre-selected (<c>size &gt; 0 &amp;&amp; !IsDestructiveHint</c>)
    /// and the view model replaces the whole collection, so a rescan silently re-ticked everything the
    /// user had unticked — immediately after the scan summary told them to "untick anything you want to
    /// keep". <c>RefreshOnF5</c> is <c>ScanCommand</c>, so pressing F5 was enough to undo their choice
    /// (#2301).
    /// <para>An empty <paramref name="previous"/> is the FIRST scan, the only time the service's default
    /// is the answer.</para>
    /// <para>Only categories that HAD something in them carry forward, and that is the load-bearing
    /// detail: an empty category was unticked by the SCAN, not by the user, so there is no decision to
    /// preserve — and if it has content now, the default should apply again. Carrying every category
    /// forward regardless would leave one that filled up since the last scan permanently unticked, for a
    /// choice its owner never made.</para>
    /// <para>Note what this deliberately does NOT key on: whether anything is currently selected. A user
    /// who unticked every category has made a decision, and reading that as "nothing chosen yet" would
    /// re-tick the lot — which is the bug itself. The same reasoning produced the drive-selection fix in
    /// <c>SystemHealthViewModel</c> (#2300); that one needs no size clause only because a drive's default
    /// is structural (C:) rather than measured.</para>
    /// </remarks>
    internal static void CarryForwardSelection(
        IReadOnlyCollection<CleanupCategory> previous, IReadOnlyCollection<CleanupCategory> fresh)
    {
        Helpers.SelectionCarry.Apply(previous, fresh, c => c.Name, StringComparer.Ordinal,
                                     carriedADecision: c => c.TotalSizeBytes > 0);
    }

    private void OnCategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategory.IsSelected))
        {
            OnPropertyChanged(nameof(TotalSelectedBytes));
            OnPropertyChanged(nameof(TotalSelectedDisplay));
            CleanCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var c in Categories)
                c.PropertyChanged -= OnCategoryPropertyChanged;
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _cleanCts?.Cancel();
            _cleanCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- deep cleanup scan ----------

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup Scan");
        if (opLock is null)
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
            var progress = new SettlingProgress<DeepCleanupService.ScanProgress>(p =>
            {
                ScanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                ScanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
                ScanEtaText = _scanEta.Update(ScanProgress);
            });
            var cats = await progress.SettleAfterAsync(reporter => _cleanup.ScanAsync(reporter, _scanCts.Token));

            // Carry the user's ticks across the rescan. Categories arrive pre-selected from the service
            // (size > 0 && !IsDestructiveHint) and ReplaceWith throws the old ones away, so a rescan
            // silently re-ticked everything the user had unticked — immediately after the summary below
            // told them to "untick anything you want to keep". RefreshOnF5 is ScanCommand, so pressing F5
            // was enough to undo their choice (#2301).
            //
            var catList = cats.ToList();

            // Applied BEFORE the new categories are subscribed, so re-applying a tick the user set
            // earlier does not fire a burst of change notifications for state they have not just changed —
            // and before ReplaceWith, which is what makes the previous ticks readable at all.
            CarryForwardSelection(Categories, catList);

            // MEM-006: Unsubscribe from old categories before clearing to prevent
            // PropertyChanged lambda leaks across rescans.
            foreach (var old in Categories)
                old.PropertyChanged -= OnCategoryPropertyChanged;
            foreach (var c in catList)
                c.PropertyChanged += OnCategoryPropertyChanged;
            Categories.ReplaceWith(catList);
            var total = cats.Sum(c => c.TotalSizeBytes);
            ScanSummary = $"Found {FormatHelper.FormatSize(total)} across {cats.Count} categories. Untick anything you want to keep.";
            ScanStatusLine = "Scan complete.";
            ToastService.Instance.Show("Deep cleanup scan complete", $"{FormatHelper.FormatSize(total)} across {cats.Count} categories");
            Log.Information("Deep cleanup scan completed: {Size} across {Count} categories",
                FormatHelper.FormatSize(total), cats.Count);
            OnPropertyChanged(nameof(TotalSelectedBytes));
            OnPropertyChanged(nameof(TotalSelectedDisplay));
            // Categories arrive pre-selected, but ReplaceWith is a collection Reset that
            // raises no per-item PropertyChanged — so CanClean (Categories.Any(IsSelected))
            // is never re-evaluated and the "Clean selected" button stays in its prior
            // (disabled) state after the first scan. Re-notify explicitly, mirroring how
            // OnCategoryPropertyChanged re-notifies when an individual tick changes.
            CleanCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) { ScanSummary = "Scan cancelled."; ScanStatusLine = "Cancelled."; }
        catch (IOException ex) { ScanSummary = $"Scan failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ScanSummary = $"Scan failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        if (IsCleaning || !Categories.Any(c => c.IsSelected)) return;

        // Deletion is permanent. Confirm first, showing how much will be removed so the choice is
        // informed — and naming the Recycle Bin explicitly whenever it is one of the selected categories.
        //
        // The previous wording was only "These files are removed directly, not sent to the Recycle Bin."
        // That was meant as "this is permanent", but it reads as "your Recycle Bin is not touched" — and
        // the "Recycle Bin (all drives)" category is SELECTED BY DEFAULT whenever the bin is non-empty
        // (DeepCleanupService pre-ticks every category that has size and no destructive hint). So the one
        // dialog standing between the user and an emptied Recycle Bin appeared to promise the opposite of
        // what pressing Clean would actually do.
        var selected = Categories.Where(c => c.IsSelected).ToList();
        var totalBytes = selected.Sum(c => c.TotalSizeBytes);
        var fileCount = selected.Sum(c => c.FileCount);
        if (!DialogService.Instance.Confirm(
                string.Create(CultureInfo.InvariantCulture, $"Permanently delete {fileCount:N0} files (~{FormatHelper.FormatSize(totalBytes)}) ") +
                $"across {selected.Count} categor{(selected.Count == 1 ? "y" : "ies")}?\n\n" +
                (selected.Any(c => c.IsRecycleBin)
                    ? "This includes emptying the Recycle Bin on every drive, so whatever is in it now " +
                      "goes too. Nothing deleted here can be recovered afterwards."
                    : "These files are deleted outright — they do not go to the Recycle Bin, so they " +
                      "cannot be recovered afterwards."),
                "Confirm Deep Cleanup"))
        {
            return;
        }

        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        if (opLock is null)
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
            var progress = new SettlingProgress<DeepCleanupService.ScanProgress>(p =>
            {
                CleanProgress = p.Total > 0 ? p.Current * 100 / p.Total : 0;
                CleanStatusLine = $"[{p.Current}/{p.Total}]  {p.CategoryName}";
                CleanEtaText = _cleanEta.Update(CleanProgress);
            });
            var result = await progress.SettleAfterAsync(
                reporter => _cleanup.CleanAsync(Categories, reporter, _cleanCts.Token));
            CleanSummary = result.Summary;
            CleanStatusLine = "Clean complete.";
            ToastService.Instance.Show("Deep cleanup complete", result.Summary);
            // This is the least reversible operation in the app — files are deleted outright, not sent
            // to the Recycle Bin — and it left no trace in the app's own history. Summary is counts and
            // sizes only, never paths: activity.json is plain text under %LocalAppData%.
            ActivityLogService.Instance.Log("Deep Cleanup", result.Summary);
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
    }
}
