// SysManager · CleanupViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class CleanupViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RescanCommand;

    private readonly IPowerShellRunner _runner;

    // The temp/Recycle-Bin sizing, behind a seam. It used to be inline here, which meant constructing this
    // view-model kicked off a recursive walk of both temp folders and every per-SID Recycle Bin folder — 30
    // times over in one unit-test file, one of which then asserted the walk finished inside fifteen seconds.
    private readonly ICleanupPreScanService _preScan;

    private readonly EtaCalculator _storeEta = new();

    private CancellationTokenSource? _tempCts;
    private CancellationTokenSource? _binCts;
    private CancellationTokenSource? _storeCts;

    // Temp Cleanup and both component-store operations stream through the single shared _runner
    // and its LineReceived/ProgressChanged events into the one Console, so only one may run at a
    // time — otherwise their output and progress cross-contaminate. The per-category
    // OperationLockService locks don't close this gap: Temp Cleanup is a Disk operation while the
    // component-store operations are SystemModification, so those locks never exclude Temp from
    // them. This intra-VM guard does. (Empty-Recycle-Bin doesn't touch _runner, so it is
    // intentionally not gated.) Set and read only on the UI thread.
    private bool _runnerBusy;

    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _isElevated;

    // Per-task running flags so buttons stay independent and the main thread doesn't block a user
    // navigating away while a component-store cleanup grinds for half an hour.
    [ObservableProperty] private bool _isTempRunning;
    [ObservableProperty] private bool _isBinRunning;
    [ObservableProperty] private bool _isStoreRunning;

    [ObservableProperty] private string _storeStatus = "Idle";
    [ObservableProperty] private string _storeVerdict = "";
    [ObservableProperty] private string _storeVerdictColorHex = StatusColors.Neutral;

    /// <summary>
    /// True once an analysis has reported that Windows itself considers a cleanup worthwhile — which is
    /// what enables the button that actually performs one.
    /// </summary>
    /// <remarks>
    /// The whole point of splitting this into two steps. Every rival runs component cleanup as one opaque
    /// click; here the read-only <c>/AnalyzeComponentStore</c> reports first, and the destructive
    /// <c>/StartComponentCleanup</c> is not even clickable until it has. So the user is told what they
    /// stand to reclaim, and what they give up, before anything is removed.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanComponentStoreCommand))]
    private bool _canCleanStore;

    [ObservableProperty] private string _storeEtaText = string.Empty;

    // Pre-scan info so the tab doesn't look empty on first load
    [ObservableProperty] private string _tempSizeLabel = "Scanning…";
    [ObservableProperty] private string _recycleBinLabel = "Scanning…";

    /// <summary>True whenever any background task is running — for a small badge.</summary>
    public bool IsAnyRunning => IsTempRunning || IsBinRunning || IsStoreRunning;

    public CleanupViewModel(IPowerShellRunner runner, ICleanupPreScanService preScan)
    {
        _runner = runner;
        _preScan = preScan;
        _runner.LineReceived += OnRunnerLineReceived;
        _runner.ProgressChanged += OnRunnerProgressChanged;
        IsElevated = AdminHelper.IsElevated();

        InitializeAsync(InitAsync);
    }

    private void OnRunnerLineReceived(PowerShellLine l) => Console.Append(l);
    private void OnRunnerProgressChanged(int p) => Progress = p;

    // Claims the shared _runner for one console/repair op; returns false if another already
    // holds it. internal for the regression test (mirrors ParseComponentStoreResult's visibility).
    internal bool TryBeginConsoleOp()
    {
        if (_runnerBusy) return false;
        _runnerBusy = true;
        return true;
    }

    internal void EndConsoleOp() => _runnerBusy = false;

    private async Task InitAsync()
    {
        // reportProgress: false — the startup scan already announces itself through the size labels,
        // which read "Scanning…" until it lands. Driving the progress bar from here would also make
        // the flag's value depend on when a fire-and-forget task happens to resume relative to the
        // constructor returning, which is not something callers (or tests) can reason about.
        try { await PreScanAsync(reportProgress: false); }
        catch (IOException ex) { Log.Warning("Cleanup pre-scan failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Cleanup pre-scan failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Cleanup pre-scan failed: {Error}", ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runner.LineReceived -= OnRunnerLineReceived;
            _runner.ProgressChanged -= OnRunnerProgressChanged;
            _tempCts?.Cancel();
            _tempCts?.Dispose();
            _binCts?.Cancel();
            _binCts?.Dispose();
            _storeCts?.Cancel();
            _storeCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task RescanAsync() => await PreScanAsync(reportProgress: true);

    /// <summary>
    /// Measures what Temp and the Recycle Bin currently hold, off the UI thread.
    /// </summary>
    /// <param name="reportProgress">
    /// Whether to drive the progress bar. True for the user-pressed Rescan — it walks every file
    /// under both Temp folders and the Recycle Bin (seconds of disk work on a neglected machine) and
    /// a button press has to visibly do something. False for the startup scan, whose progress is
    /// already visible in the "Scanning…" size labels, and which runs fire-and-forget from the
    /// constructor: touching the flag there would make its value depend on when that task resumes
    /// relative to construction finishing.
    /// <para>Separate from <c>OnAnyRunningChanged</c>'s derived flag either way — a pre-scan is not
    /// one of the cleanup operations.</para>
    /// </param>
    private async Task PreScanAsync(bool reportProgress)
    {
        if (reportProgress)
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
        }
        try
        {
            var measured = await _preScan.MeasureAsync();

            // Assigned on the calling (UI) thread so PropertyChanged fires correctly.
            TempSizeLabel = measured.TempLabel;
            RecycleBinLabel = measured.RecycleBinLabel;
        }
        catch (IOException ex) { Log.Debug("Pre-scan failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Pre-scan access denied: {Error}", ex.Message); }
        // Hand the flag back to the DERIVED value rather than blindly clearing it: a cleanup
        // operation may have started while the scan ran, and it still needs the bar. Skipped when we
        // never raised the flag, so the startup scan cannot disturb it either.
        finally { if (reportProgress) OnAnyRunningChanged(); }
    }

    // Each running flag feeds IsAnyRunning; re-evaluate Cancel's CanExecute too so the
    // button is disabled when nothing is running and enabled the moment a task starts.
    partial void OnIsTempRunningChanged(bool value) => OnAnyRunningChanged();
    partial void OnIsBinRunningChanged(bool value) => OnAnyRunningChanged();
    partial void OnIsStoreRunningChanged(bool value)
    {
        OnAnyRunningChanged();
        // Analyse and clean are separate commands over one running flag, so both re-evaluate:
        // neither may be clickable a second time while the other is mid-flight.
        AnalyzeComponentStoreCommand.NotifyCanExecuteChanged();
        CleanComponentStoreCommand.NotifyCanExecuteChanged();
    }

    private void OnAnyRunningChanged()
    {
        OnPropertyChanged(nameof(IsAnyRunning));
        CancelCommand.NotifyCanExecuteChanged();
        // The status-bar progress bar and the sidebar spinner are bound to IsBusy, which this VM
        // never set — so neither could appear while a component-store operation ran, which is up to
        // half an hour of work. Derived from the per-operation flags rather than assigned in each
        // command, so the operations can overlap without one finishing and clearing the bar for
        // the others.
        IsBusy = IsAnyRunning;
        // Temp and Recycle-Bin cleanup report no percentage, but the component-store operations do
        // (DISM's decimal percentage, parsed off the runner's output). Marquee only when nothing is
        // reporting a real number, otherwise the determinate value would be ignored.
        IsProgressIndeterminate = IsAnyRunning && !IsStoreRunning;
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    [RelayCommand]
    private async Task CleanTempAsync()
    {
        if (IsTempRunning) return;
        if (!DialogService.Instance.Confirm(
                "Delete temporary files from your user and Windows Temp folders?\n\n" +
                "Files in use may be skipped. This cannot be undone.",
                "Confirm Temp Cleanup"))
        {
            return;
        }
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Temp Cleanup");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }
        if (!TryBeginConsoleOp())
        {
            StatusMessage = "Cannot start — a repair is already using the console. Wait for it to finish.";
            return;
        }
        IsTempRunning = true;
        StatusMessage = "Cleaning temp folders...";
        _tempCts?.Dispose();
        _tempCts = new CancellationTokenSource();
        try
        {
            // Delegated to TuneUpService rather than kept as a third temp sweeper. The inline PowerShell this
            // replaces walked %TEMP% with no exclusions at all, while both C# sweepers pass
            // SystemPaths.BundleExtractionRoot and SystemPaths.OwnExtractionDirectory — so this command could
            // delete the .NET single-file extraction root, its own and that of every other running single-file
            // app, which is the failure SystemPaths documents (an app whose payload is extracted but not yet
            // loaded cannot be detected as in use).
            //
            // It also fixes the reported total. The script did `$totalBytes += $c.Length` BEFORE
            // `Remove-Item -ErrorAction SilentlyContinue`, and SilentlyContinue makes a locked file a
            // non-terminating error, so the empty catch never fired and a file it failed to delete was still
            // counted as freed — while the confirmation dialog above says "Files in use may be skipped".
            // CleanTempFiles captures the size, deletes, and only then adds, counting failures separately.
            Console.Append(PowerShellLine.Output("Cleaning user and Windows Temp folders..."));
            var (bytesFreed, filesDeleted, errors) = await TuneUpService.CleanTempFilesAsync(_tempCts.Token);
            Console.Append(PowerShellLine.Output(string.Create(CultureInfo.InvariantCulture,
                $"Freed {bytesFreed / 1024.0 / 1024.0:F1} MB across {filesDeleted} file(s).")));
            if (errors > 0)
            {
                Console.Append(PowerShellLine.Output(string.Create(CultureInfo.InvariantCulture,
                    $"{errors} file(s) were in use or protected and were skipped.")));
            }

            StatusMessage = "Temp cleanup done";
            Log.Information("Temp cleanup completed");
            ActivityLogService.Instance.Log("Quick Cleanup", "Cleared temporary files");
            // The operation's own flag still holds the bar (it clears in finally), so this
            // refresh must not take it over.
            await PreScanAsync(reportProgress: false);
        }
        catch (OperationCanceledException) { StatusMessage = "Temp cleanup cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { EndConsoleOp(); IsTempRunning = false; }
    }

    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        if (IsBinRunning) return;
        if (!DialogService.Instance.Confirm(
                "Permanently empty the Recycle Bin? Its contents cannot be recovered.",
                "Confirm Empty Recycle Bin"))
        {
            return;
        }
        IsBinRunning = true;
        StatusMessage = "Emptying Recycle Bin...";
        _binCts?.Dispose();
        _binCts = new CancellationTokenSource();
        try
        {
            // Use the shared shell-API helper (the single source of truth) rather than an
            // inline Clear-RecycleBin: the shell API reliably removes ghosted entries that
            // Clear-RecycleBin can leave behind, and keeps this in step with Deep Cleanup
            // and the One-Click Tune-Up. Run off the UI thread.
            var ct = _binCts.Token;
            // EmptyAllDrives reports failure through its RETURN VALUE, not an exception:
            // SHEmptyRecycleBin is a LibraryImport returning an HRESULT, so there is nothing to catch.
            // The result used to be discarded, which meant "Done — Operation finished successfully"
            // appeared even when the shell refused and the bin was still full. A cleanup tool claiming
            // it cleaned when it did not is the one thing it must never do.
            var emptied = await Task.Run(RecycleBinHelper.EmptyAllDrives, ct);
            if (emptied)
            {
                StatusMessage = "Done";
                ToastService.Instance.Show("Cleanup complete", "Operation finished successfully");
            }
            else
            {
                StatusMessage = "Could not empty the Recycle Bin — Windows refused the request.";
                ToastService.Instance.Show("Recycle Bin not emptied",
                    "Windows would not empty it. It may be open in Explorer, or a file may be in use.");
                Log.Warning("Empty Recycle Bin: the shell API reported failure");
            }
            // The operation's own flag still holds the bar (it clears in finally), so this
            // refresh must not take it over. Runs either way, so the size shown matches reality
            // whether the empty succeeded or not.
            await PreScanAsync(reportProgress: false);
        }
        catch (OperationCanceledException) { StatusMessage = "Recycle Bin cleanup cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBinRunning = false; }
    }

    // ---------- component store (WinSxS) ----------

    /// <summary>
    /// Asks DISM what the component store holds and whether it thinks a cleanup is worth doing. Read-only.
    /// </summary>
    /// <remarks>
    /// WinSxS routinely holds several gigabytes of superseded components, and it is the one place the free
    /// editions of the mainstream cleaners beat this app on the headline number. They also run the cleanup
    /// as a single opaque click. This reports first — Windows' own numbers, with the cost stated — and
    /// leaves the removal to a second, separate press.
    /// <para>Never given <c>/ResetBase</c>: that variant also discards the ability to uninstall installed
    /// updates, which is not something a cleanup button may decide on the user's behalf. Enforced by
    /// <c>NoDismCall_PassesResetBase_AndEveryWindowsRepairCommandIsBound</c> rather than left to review.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunStoreOp))]
    private Task AnalyzeComponentStoreAsync()
        => RunComponentStoreAsync("/Online /Cleanup-Image /AnalyzeComponentStore", analyzing: true);

    /// <summary>Performs the cleanup the analysis reported, after an explicit confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanComponentStore))]
    private async Task CleanComponentStoreAsync()
    {
        if (IsStoreRunning) return;

        // Confirmed because it is not reversible in the way the rest of the tab is: superseded component
        // versions are what get removed, and those are exactly what an update uninstall would roll back to.
        if (!DialogService.Instance.Confirm(
                "Windows will remove the superseded components it is keeping in the component store.\n\n"
                + "What you give up: updates already installed can no longer be uninstalled afterwards. "
                + "Nothing you have installed stops working, and no personal files are touched.\n\n"
                + "This can take 10 to 30 minutes. Cancelling stops the process mid-way, which leaves "
                + "Windows to finish the servicing transaction on its own — better to let it run.",
                "Clean up the component store?"))
        {
            StatusMessage = "Component store cleanup cancelled.";
            return;
        }

        await RunComponentStoreAsync("/Online /Cleanup-Image /StartComponentCleanup", analyzing: false);
    }

    /// <summary>Analyse and clean differ only in the DISM argument and the wording, so they share this.</summary>
    private async Task RunComponentStoreAsync(string arguments, bool analyzing)
    {
        if (IsStoreRunning) return;
        if (!AdminHelper.IsElevated())
        {
            if (!DialogService.Instance.Confirm(
                "Working with the component store requires admin privileges. Restart the application with elevated privileges?",
                "Admin Required"))
            {
                StatusMessage = "Component store check cancelled — admin privileges required.";
                return;
            }
            if (AdminHelper.RelaunchAsAdmin()) App.RequestShutdown();
            return;
        }

        // Two guards, two reasons. The SystemModification lock is cross-TAB: System Fixes' SFC and
        // DISM /RestoreHealth hold the same one, and running either against the online image while
        // this rearranges the component store would have them fight over the same servicing stack.
        // _runnerBusy is intra-VM: it keeps Temp Cleanup — a Disk operation, so not excluded by that
        // lock — off the single shared runner whose LineReceived feeds the one Console.
        using var opLock = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, analyzing ? "Component store analysis" : "Component store cleanup");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }
        if (!TryBeginConsoleOp())
        {
            StatusMessage = "Cannot start — a repair is already using the console. Wait for it to finish.";
            return;
        }

        IsStoreRunning = true;
        IsProgressIndeterminate = true;
        StoreStatus = analyzing ? "Analysing — usually under a minute" : "Cleaning up — can take 10–30 minutes";
        StoreVerdict = "";
        StoreVerdictColorHex = StatusColors.Neutral;
        StoreEtaText = string.Empty;
        _storeEta.Reset();
        StatusMessage = analyzing
            ? "Analysing the component store. You can keep using the app."
            : "Cleaning up the component store in the background. You can keep using the app.";
        _storeCts?.Dispose();
        _storeCts = new CancellationTokenSource();
        var captured = new System.Collections.Generic.List<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
            if (l.Text.Contains('%'))
            {
                var m = DismPercentRegex().Match(l.Text);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct is >= 0 and <= 100)
                {
                    Progress = (int)pct;
                    StoreEtaText = _storeEta.Update((int)pct);
                    IsProgressIndeterminate = false;
                }
            }
        }
        _runner.LineReceived += Collect;
        try
        {
            var exit = await _runner.RunProcessAsync("DISM.exe", arguments, _storeCts.Token, PowerShellRunner.OemEncoding);
            var result = ParseComponentStoreResult(captured, exit, analyzing);
            StoreVerdict = result.Verdict;
            StoreVerdictColorHex = result.ColorHex;
            StoreStatus = exit == 0 ? "Completed" : $"Finished (exit {exit})";
            StatusMessage = result.Verdict;
            // Only an analysis may enable the cleanup, and only when Windows said it is worth doing. A
            // completed cleanup clears it: the analysis it was based on is now stale, and offering to
            // clean again on the strength of it would report a size that no longer exists.
            CanCleanStore = analyzing && result.CleanupRecommended;
            if (!analyzing)
            {
                ActivityLogService.Instance.Log("Component store cleanup", result.Verdict);
            }
        }
        catch (OperationCanceledException) { StoreStatus = "Cancelled."; StoreVerdict = analyzing ? "Analysis was cancelled." : "Cleanup was cancelled — Windows will finish the servicing transaction itself."; StoreVerdictColorHex = StatusColors.Neutral; StatusMessage = StoreStatus; }
        catch (InvalidOperationException ex) { StoreStatus = $"Error: {ex.Message}"; StoreVerdict = ex.Message; StoreVerdictColorHex = StatusColors.Bad; StatusMessage = StoreStatus; }
        catch (System.ComponentModel.Win32Exception ex) { StoreStatus = $"Error: {ex.Message}"; StoreVerdict = ex.Message; StoreVerdictColorHex = StatusColors.Bad; StatusMessage = StoreStatus; }
        finally { _runner.LineReceived -= Collect; EndConsoleOp(); IsStoreRunning = false; IsProgressIndeterminate = false; StoreEtaText = string.Empty; }
    }

    /// <summary>Neither component-store operation may start while one is already running.</summary>
    private bool CanRunStoreOp => !IsStoreRunning;

    /// <summary>The cleanup additionally needs an analysis that recommended it.</summary>
    private bool CanCleanComponentStore => !IsStoreRunning && CanCleanStore;

    /// <summary>
    /// Turns <c>/AnalyzeComponentStore</c> or <c>/StartComponentCleanup</c> output into a verdict, a colour,
    /// and — for an analysis — whether the cleanup button should become available.
    /// </summary>
    /// <remarks>
    /// Matches on the English phrases DISM prints, the same approach and the same limitation as
    /// <see cref="SystemFixesViewModel.ParseSfcResult"/> and
    /// <see cref="SystemFixesViewModel.ParseDismResult"/>, with an exit-code fallback for
    /// everything else. <c>Component Store Cleanup Recommended : No</c> is a real and common answer, and
    /// saying so is more useful than an empty result — a store already cleaned should not present a button
    /// that would spend half an hour reclaiming nothing.
    /// <para>The reclaimable size is quoted from <c>Actual Size of Component Store</c> rather than computed.
    /// Windows' own number is the honest one to show, and the alternative — subtracting "Shared with
    /// Windows" — would state a saving this app cannot actually promise.</para>
    /// </remarks>
    internal static (string Verdict, string ColorHex, bool CleanupRecommended) ParseComponentStoreResult(
        IReadOnlyList<string> lines, int exitCode, bool analyzing)
    {
        var all = string.Join(" ", lines);

        if (!analyzing)
        {
            if (all.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase))
                return ("Component store cleaned up. The space Windows reported as reclaimable is now free.", StatusColors.Good, false);

            return exitCode == 0
                ? ("Component store cleanup finished.", StatusColors.Good, false)
                : ($"Component store cleanup finished with exit code {exitCode}. Check the console output for details.", StatusColors.Warning, false);
        }

        var recommended = all.Contains("Component Store Cleanup Recommended : Yes", StringComparison.OrdinalIgnoreCase);
        var notRecommended = all.Contains("Component Store Cleanup Recommended : No", StringComparison.OrdinalIgnoreCase);
        var size = StoreSizeRegex().Match(all);
        var sizeText = size.Success ? size.Groups[1].Value.Trim() : null;

        if (recommended)
        {
            return (sizeText is null
                    ? "Windows recommends cleaning up the component store."
                    : $"Windows recommends cleaning up the component store, which currently holds {sizeText}.",
                StatusColors.Warning, true);
        }

        if (notRecommended)
        {
            return (sizeText is null
                    ? "No cleanup needed — Windows says the component store is already as small as it will get."
                    : $"No cleanup needed — the component store holds {sizeText}, and Windows says none of it is worth reclaiming.",
                StatusColors.Good, false);
        }

        return exitCode == 0
            ? ("Analysis completed. Check the console output for what Windows reported.", StatusColors.Neutral, false)
            : ($"Analysis finished with exit code {exitCode}. Check the console output for details.", StatusColors.Warning, false);
    }

    [RelayCommand(CanExecute = nameof(IsAnyRunning))]
    private void Cancel()
    {
        _tempCts?.Cancel();
        _binCts?.Cancel();
        _storeCts?.Cancel();
    }

    // DISM reports progress as a decimal percentage, e.g. "50.0%".
    [GeneratedRegex(@"([\d.]+)%")]
    private static partial Regex DismPercentRegex();

    // "Actual Size of Component Store : 7.90 GB" — the size Windows itself reports, quoted rather than
    // recomputed. Bounded to a number-and-unit so a localised or reworded line yields no match and the
    // verdict simply omits the size, instead of quoting whatever followed the colon.
    [GeneratedRegex(@"Actual Size of Component Store\s*:\s*([\d.,]+\s*[KMGT]?B)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreSizeRegex();
}
