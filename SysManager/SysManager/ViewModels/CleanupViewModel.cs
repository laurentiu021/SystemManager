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
    private readonly IPowerShellRunner _runner;

    // The temp/Recycle-Bin sizing, behind a seam. It used to be inline here, which meant constructing this
    // view-model kicked off a recursive walk of both temp folders and every per-SID Recycle Bin folder — 30
    // times over in one unit-test file, one of which then asserted the walk finished inside fifteen seconds.
    private readonly ICleanupPreScanService _preScan;

    private readonly EtaCalculator _sfcEta = new();
    private readonly EtaCalculator _dismEta = new();

    private CancellationTokenSource? _tempCts;
    private CancellationTokenSource? _binCts;
    private CancellationTokenSource? _sfcCts;
    private CancellationTokenSource? _dismCts;

    // Temp Cleanup, SFC, and DISM all stream through the single shared _runner and its
    // LineReceived/ProgressChanged events into the one Console, so only one may run at a
    // time — otherwise their output and progress cross-contaminate. The per-category
    // OperationLockService locks don't close this gap: Temp Cleanup is a Disk operation
    // while SFC/DISM are SystemModification, so those locks never exclude Temp from
    // SFC/DISM. This intra-VM guard does. (Empty-Recycle-Bin doesn't touch _runner, so it
    // is intentionally not gated.) Set and read only on the UI thread.
    private bool _runnerBusy;

    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _isElevated;

    // Per-task running flags so buttons stay independent and the main thread
    // doesn't block a user navigating away while SFC grinds for 10 minutes.
    [ObservableProperty] private bool _isTempRunning;
    [ObservableProperty] private bool _isBinRunning;
    [ObservableProperty] private bool _isSfcRunning;
    [ObservableProperty] private bool _isDismRunning;

    [ObservableProperty] private string _sfcStatus = "Idle";
    [ObservableProperty] private string _sfcVerdict = "";
    [ObservableProperty] private string _sfcVerdictColorHex = StatusColors.Neutral;
    [ObservableProperty] private string _dismStatus = "Idle";
    [ObservableProperty] private string _dismVerdict = "";
    [ObservableProperty] private string _dismVerdictColorHex = StatusColors.Neutral;

    [ObservableProperty] private string _sfcEtaText = string.Empty;
    [ObservableProperty] private string _dismEtaText = string.Empty;

    // Pre-scan info so the tab doesn't look empty on first load
    [ObservableProperty] private string _tempSizeLabel = "Scanning…";
    [ObservableProperty] private string _recycleBinLabel = "Scanning…";

    /// <summary>True whenever any background task is running — for a small badge.</summary>
    public bool IsAnyRunning => IsTempRunning || IsBinRunning || IsSfcRunning || IsDismRunning;

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
    // holds it. internal for the regression test (mirrors ParseSfcResult's test visibility).
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
            _sfcCts?.Cancel();
            _sfcCts?.Dispose();
            _dismCts?.Cancel();
            _dismCts?.Dispose();
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
    /// one of the four cleanup operations.</para>
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
    partial void OnIsSfcRunningChanged(bool value) => OnAnyRunningChanged();
    partial void OnIsDismRunningChanged(bool value) => OnAnyRunningChanged();

    private void OnAnyRunningChanged()
    {
        OnPropertyChanged(nameof(IsAnyRunning));
        CancelCommand.NotifyCanExecuteChanged();
        // The status-bar progress bar and the sidebar spinner are bound to IsBusy, which this VM
        // never set — so neither could appear while SFC or DISM ran, which is minutes of work.
        // Derived from the per-operation flags rather than assigned in each command, so the four
        // operations can overlap without one finishing and clearing the bar for the others.
        IsBusy = IsAnyRunning;
        // Temp and Recycle-Bin cleanup report no percentage, but SFC/DISM do (via the runner's
        // ProgressChanged → Progress). Marquee only when nothing is reporting a real number,
        // otherwise the determinate value would be ignored.
        IsProgressIndeterminate = IsAnyRunning && !(IsSfcRunning || IsDismRunning);
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

    [RelayCommand]
    private async Task RunSfcAsync()
    {
        if (IsSfcRunning) return;
        if (!AdminHelper.IsElevated())
        {
            if (!DialogService.Instance.Confirm(
                "SFC requires admin privileges. Restart the application with elevated privileges?",
                "Admin Required"))
            {
                StatusMessage = "SFC cancelled — admin privileges required.";
                return;
            }
            if (AdminHelper.RelaunchAsAdmin()) App.RequestShutdown();
            return;
        }

        // SFC and DISM share the single _runner (and its LineReceived event) with each other
        // AND with Temp Cleanup, so running two at once cross-contaminates their captured
        // output and progress. The SystemModification lock makes SFC and DISM mutually
        // exclusive (and blocks the other system-repair operations); the _runnerBusy guard
        // below additionally excludes Temp Cleanup, which holds a different (Disk) lock and
        // so is not covered by this one.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
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

        IsSfcRunning = true;
        IsProgressIndeterminate = true;
        SfcStatus = "Running — can take 5–15 minutes";
        SfcVerdict = "";
        SfcVerdictColorHex = StatusColors.Neutral;
        SfcEtaText = string.Empty;
        _sfcEta.Reset();
        StatusMessage = "SFC running in background. You can keep using the app.";
        _sfcCts?.Dispose();
        _sfcCts = new CancellationTokenSource();
        var captured = new System.Collections.Generic.List<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
            if (l.Text.Contains('%') || l.Text.Contains("complete", StringComparison.OrdinalIgnoreCase))
            {
                var m = SfcPercentRegex().Match(l.Text);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pct) && pct is >= 0 and <= 100)
                {
                    Progress = pct;
                    SfcEtaText = _sfcEta.Update(pct);
                    IsProgressIndeterminate = false;
                }
            }
        }
        _runner.LineReceived += Collect;
        try
        {
            var exit = await _runner.RunProcessAsync("sfc.exe", "/scannow", _sfcCts.Token, PowerShellRunner.OemEncoding);
            var (verdict, color) = ParseSfcResult(captured, exit);
            SfcVerdict = verdict;
            SfcVerdictColorHex = color;
            SfcStatus = exit == 0 ? "Completed" : $"Finished (exit {exit})";
            StatusMessage = verdict;
        }
        catch (OperationCanceledException) { SfcStatus = "Cancelled."; SfcVerdict = "Scan was cancelled."; SfcVerdictColorHex = StatusColors.Neutral; StatusMessage = SfcStatus; }
        catch (InvalidOperationException ex) { SfcStatus = $"Error: {ex.Message}"; SfcVerdict = ex.Message; SfcVerdictColorHex = StatusColors.Bad; StatusMessage = SfcStatus; }
        catch (System.ComponentModel.Win32Exception ex) { SfcStatus = $"Error: {ex.Message}"; SfcVerdict = ex.Message; SfcVerdictColorHex = StatusColors.Bad; StatusMessage = SfcStatus; }
        finally { _runner.LineReceived -= Collect; EndConsoleOp(); IsSfcRunning = false; IsProgressIndeterminate = false; SfcEtaText = string.Empty; }
    }

    /// <summary>
    /// Parses the captured SFC output lines to produce a human-readable verdict
    /// with an appropriate color. SFC writes its results in the OEM code page,
    /// so we match on key phrases that appear in all locales.
    /// </summary>
    internal static (string Verdict, string ColorHex) ParseSfcResult(IReadOnlyList<string> lines, int exitCode)
    {
        var all = string.Join(" ", lines);

        // "did not find any integrity violations"
        if (all.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
            return ("No integrity violations found — your system files are healthy.", StatusColors.Good);

        // "found corrupt files and successfully repaired them"
        if (all.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase))
            return ("Corrupted files were found and successfully repaired.", StatusColors.Warning);

        // "found corrupt files but was unable to fix some of them"
        if (all.Contains("unable to fix", StringComparison.OrdinalIgnoreCase))
            return ("Corrupted files found but SFC could not repair them. Try running DISM /RestoreHealth first, then SFC again.", StatusColors.Bad);

        // "could not perform the requested operation"
        if (all.Contains("could not perform", StringComparison.OrdinalIgnoreCase))
            return ("SFC could not run. Try rebooting into Safe Mode or running DISM first.", StatusColors.Bad);

        // Fallback based on exit code
        return exitCode == 0
            ? ("Scan completed successfully.", StatusColors.Good)
            : ($"Scan finished with exit code {exitCode}. Check the console output for details.", StatusColors.Warning);
    }

    [RelayCommand]
    private async Task RunDismAsync()
    {
        if (IsDismRunning) return;
        if (!AdminHelper.IsElevated())
        {
            if (!DialogService.Instance.Confirm(
                "DISM requires admin privileges. Restart the application with elevated privileges?",
                "Admin Required"))
            {
                StatusMessage = "DISM cancelled — admin privileges required.";
                return;
            }
            if (AdminHelper.RelaunchAsAdmin()) App.RequestShutdown();
            return;
        }

        // Mutually exclusive with SFC and the other system-repair ops (SystemModification
        // lock) AND with Temp Cleanup (the _runnerBusy guard below): all three share the
        // single _runner and its LineReceived event, so concurrent runs would cross-
        // contaminate captured output and progress. Temp holds a different (Disk) lock, so
        // only the guard — not this lock — excludes it.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
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

        IsDismRunning = true;
        IsProgressIndeterminate = true;
        DismStatus = "Running — can take 10–30 minutes";
        DismVerdict = "";
        DismVerdictColorHex = StatusColors.Neutral;
        DismEtaText = string.Empty;
        _dismEta.Reset();
        StatusMessage = "DISM running in background. You can keep using the app.";
        _dismCts?.Dispose();
        _dismCts = new CancellationTokenSource();
        var captured = new System.Collections.Generic.List<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
            if (l.Text.Contains('%'))
            {
                var m = DismPercentRegex().Match(l.Text);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct is >= 0 and <= 100)
                {
                    Progress = (int)pct;
                    DismEtaText = _dismEta.Update((int)pct);
                    IsProgressIndeterminate = false;
                }
            }
        }
        _runner.LineReceived += Collect;
        try
        {
            var exit = await _runner.RunProcessAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", _dismCts.Token, PowerShellRunner.OemEncoding);
            var (verdict, color) = ParseDismResult(captured, exit);
            DismVerdict = verdict;
            DismVerdictColorHex = color;
            DismStatus = exit == 0 ? "Completed" : $"Finished (exit {exit})";
            StatusMessage = verdict;
        }
        catch (OperationCanceledException) { DismStatus = "Cancelled."; DismVerdict = "Repair was cancelled."; DismVerdictColorHex = StatusColors.Neutral; StatusMessage = DismStatus; }
        catch (InvalidOperationException ex) { DismStatus = $"Error: {ex.Message}"; DismVerdict = ex.Message; DismVerdictColorHex = StatusColors.Bad; StatusMessage = DismStatus; }
        catch (System.ComponentModel.Win32Exception ex) { DismStatus = $"Error: {ex.Message}"; DismVerdict = ex.Message; DismVerdictColorHex = StatusColors.Bad; StatusMessage = DismStatus; }
        finally { _runner.LineReceived -= Collect; EndConsoleOp(); IsDismRunning = false; IsProgressIndeterminate = false; DismEtaText = string.Empty; }
    }

    /// <summary>
    /// Parses DISM RestoreHealth output into a verdict with color.
    /// </summary>
    internal static (string Verdict, string ColorHex) ParseDismResult(IReadOnlyList<string> lines, int exitCode)
    {
        var all = string.Join(" ", lines);

        if (all.Contains("The restore operation completed successfully", StringComparison.OrdinalIgnoreCase))
            return ("Component store is healthy — no repairs needed.", StatusColors.Good);

        if (all.Contains("The component store corruption was repaired", StringComparison.OrdinalIgnoreCase))
            return ("Component store was corrupted and has been repaired. Run SFC /scannow next.", StatusColors.Warning);

        if (all.Contains("source files could not be found", StringComparison.OrdinalIgnoreCase))
            return ("DISM could not find source files for repair. Try connecting to the internet or using a Windows ISO.", StatusColors.Bad);

        return exitCode == 0
            ? ("Repair completed successfully.", StatusColors.Good)
            : ($"DISM finished with exit code {exitCode}. Check the console output for details.", StatusColors.Warning);
    }

    [RelayCommand(CanExecute = nameof(IsAnyRunning))]
    private void Cancel()
    {
        _tempCts?.Cancel();
        _binCts?.Cancel();
        _sfcCts?.Cancel();
        _dismCts?.Cancel();
    }

    // SFC reports progress as a whole-number percentage, e.g. "50 %".
    [GeneratedRegex(@"(\d+)\s*%")]
    private static partial Regex SfcPercentRegex();

    // DISM reports progress as a decimal percentage, e.g. "50.0%".
    [GeneratedRegex(@"([\d.]+)%")]
    private static partial Regex DismPercentRegex();
}
