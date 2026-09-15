// SysManager · SystemFixesViewModel — one-click repairs for common Windows breakages
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the System Fixes tab — the one place in the app that repairs a broken Windows.
/// Surfaces Windows' own two general-purpose repairs (SFC and DISM /RestoreHealth), two targeted
/// ones (Windows Update, WinGet), plus a secure shortcut to the built-in auto-logon dialog. Each
/// repair confirms first, streams its output to the shared console, and reports its result
/// honestly. Repairs require administrator rights; the tab shows the standard elevation banner.
/// </summary>
/// <remarks>
/// SFC and DISM used to live on Quick Cleanup, whose own XAML called them "distinct from cleanup"
/// while the tab subtitle promised a 5-15 minute scan under the word "Quick" (#1493). A user who
/// reads tab names literally — the one this app is built for — opens System Fixes when something
/// is broken, and the two repairs most likely to fix it were on the tab named after housekeeping.
/// <para>Quick Cleanup keeps the component-store operations, and that is not an inconsistency:
/// <c>/AnalyzeComponentStore</c> and <c>/StartComponentCleanup</c> happen to be DISM too, but what
/// they do is reclaim disk space. The split here is by what the user is trying to achieve, not by
/// which executable does it.</para>
/// <para>Network-stack reset (Winsock/TCP-IP/DNS flush) lives on the Network Repair tab, which owns
/// it, so it is not duplicated here — the card list carries a pointer to it instead.</para>
/// </remarks>
public sealed partial class SystemFixesViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly SystemFixService _service;

    /// <summary>
    /// The runner SFC and DISM drive directly. Separate from the one inside
    /// <see cref="SystemFixService"/> because <see cref="IPowerShellRunner"/> is registered
    /// Transient precisely so two consumers cannot cross-contaminate each other's
    /// <c>LineReceived</c> stream. Both feed the one <see cref="Console"/>, which is safe because
    /// only one repair on this tab may run at a time (see <see cref="CanRunFix"/>).
    /// </summary>
    private readonly IPowerShellRunner _runner;

    private readonly EtaCalculator _sfcEta = new();
    private readonly EtaCalculator _dismEta = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _sfcCts;
    private CancellationTokenSource? _dismCts;

    [ObservableProperty] private bool _isElevated;

    // Per-repair running flags. Two reasons they are not just IsBusy: the SFC and DISM indicators
    // and verdicts are bound to their own flag, and a repair that runs for half an hour has to be
    // able to say WHICH one is running while the user is on another tab.
    [ObservableProperty] private bool _isFixRunning;
    [ObservableProperty] private bool _isSfcRunning;
    [ObservableProperty] private bool _isDismRunning;

    [ObservableProperty] private string _sfcStatus = "Idle";
    [ObservableProperty] private string _sfcVerdict = "";
    [ObservableProperty] private string _sfcVerdictColorHex = StatusColors.Neutral;
    [ObservableProperty] private string _sfcEtaText = string.Empty;

    [ObservableProperty] private string _dismStatus = "Idle";
    [ObservableProperty] private string _dismVerdict = "";
    [ObservableProperty] private string _dismVerdictColorHex = StatusColors.Neutral;
    [ObservableProperty] private string _dismEtaText = string.Empty;

    /// <summary>True while any repair on this tab is running.</summary>
    public bool IsAnyRunning => IsFixRunning || IsSfcRunning || IsDismRunning;

    /// <summary>Shared, capped, thread-safe console — same control the other repair/scan tabs use.</summary>
    public ConsoleViewModel Console { get; } = new();

    public SystemFixesViewModel(SystemFixService service, IPowerShellRunner runner)
    {
        _service = service;
        _runner = runner;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Pick a repair. Each asks for confirmation before it runs.";
        _service.LineReceived += OnLine;
        _runner.LineReceived += OnLine;
    }

    /// <summary>
    /// Every repair is exclusive: they all stream into the one console and drive the one progress
    /// bar, so a second concurrent repair would interleave its output with the first's.
    /// </summary>
    private bool CanRunFix => !IsAnyRunning && IsElevated;

    partial void OnIsElevatedChanged(bool value) => NotifyFixCommands();
    partial void OnIsFixRunningChanged(bool value) => OnAnyRunningChanged();
    partial void OnIsSfcRunningChanged(bool value) => OnAnyRunningChanged();
    partial void OnIsDismRunningChanged(bool value) => OnAnyRunningChanged();

    private void OnAnyRunningChanged()
    {
        OnPropertyChanged(nameof(IsAnyRunning));
        // Derived rather than assigned per-command so that the status-bar progress bar and the
        // sidebar spinner track the tab as a whole, and one repair finishing cannot clear the bar
        // out from under another.
        IsBusy = IsAnyRunning;
        NotifyFixCommands();
    }

    /// <summary>
    /// In-body elevation gate. Returns false, and says why, when the repair cannot run.
    /// </summary>
    /// <remarks>
    /// <see cref="CanRunFix"/> is a UI affordance, not a guard: <c>ExecuteAsync</c> runs the command
    /// body whether or not <c>CanExecute</c> agreed, so a repair that mutates system state cannot
    /// rely on the button being disabled. No confirmation dialog here — the tab's AdminBanner already
    /// offers to relaunch elevated, and a modal prompt behind a disabled button is unreachable anyway.
    /// </remarks>
    private bool RequireElevation(string what)
    {
        if (IsElevated) return true;
        StatusMessage = $"{what} needs administrator rights — use \"Restart as administrator\" above.";
        return false;
    }

    private void NotifyFixCommands()
    {
        RunSfcCommand.NotifyCanExecuteChanged();
        RunDismCommand.NotifyCanExecuteChanged();
        ResetWindowsUpdateCommand.NotifyCanExecuteChanged();
        ReinstallWinGetCommand.NotifyCanExecuteChanged();
    }

    // ConsoleViewModel.Append marshals to the UI thread, locks, and caps the line count —
    // replacing the old per-line synchronous Dispatcher.Invoke + O(n^2) string concatenation.
    private void OnLine(PowerShellLine line) => Console.Append(line);

    // ---------- Windows' own general-purpose repairs ----------

    /// <summary>
    /// Runs <c>sfc.exe /scannow</c>: verifies every protected Windows system file against its
    /// known-good copy and replaces the damaged ones.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunSfcAsync()
    {
        if (IsSfcRunning) return;
        if (!RequireElevation("Repairing Windows files")) return;

        // Cross-TAB exclusion. Quick Cleanup's component-store operations are the other holder of
        // this lock, and they are DISM against the same online image, so they must not overlap
        // with a repair. The CanRunFix gate above only covers this tab.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
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
        var captured = new List<string>();
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
        finally { _runner.LineReceived -= Collect; IsSfcRunning = false; IsProgressIndeterminate = false; SfcEtaText = string.Empty; }
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

    /// <summary>
    /// Runs <c>DISM /Online /Cleanup-Image /RestoreHealth</c>: repairs the component store SFC
    /// draws its known-good copies from, using Windows Update as the source.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunDismAsync()
    {
        if (IsDismRunning) return;
        if (!RequireElevation("Repairing the component store")) return;

        // Same cross-tab lock as SFC, for the same reason — see RunSfcAsync.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
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
        var captured = new List<string>();
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
        finally { _runner.LineReceived -= Collect; IsDismRunning = false; IsProgressIndeterminate = false; DismEtaText = string.Empty; }
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

    // ---------- targeted, scripted repairs ----------

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private Task ResetWindowsUpdateAsync() => RunFixAsync(
        "Reset Windows Update?",
        "This stops the Windows Update services, clears their cache folders " +
        "(SoftwareDistribution and catroot2, renamed so Windows rebuilds them), and restarts " +
        "the services. Pending updates will re-download. A reboot is recommended afterwards.\n\nContinue?",
        ct => _service.ResetWindowsUpdateAsync(ct));

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private Task ReinstallWinGetAsync() => RunFixAsync(
        "Reinstall WinGet?",
        "This re-registers the Windows Package Manager (App Installer) for your account, " +
        "which fixes most cases where app installs or uninstalls fail. No reboot needed.\n\nContinue?",
        ct => _service.ReinstallWinGetAsync(ct));

    private async Task RunFixAsync(string title, string message, Func<CancellationToken, Task<SystemFixResult>> fix)
    {
        if (IsFixRunning) return;
        if (!RequireElevation(title.TrimEnd('?'))) return;
        if (!DialogService.Instance.Confirm(message, title))
        {
            StatusMessage = "Cancelled.";
            return;
        }

        IsFixRunning = true;
        IsProgressIndeterminate = true;
        Console.ClearCommand.Execute(null);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var result = await fix(_cts.Token).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = result.NeedsReboot
                    ? $"{result.FixName} completed — reboot to finish."
                    : $"{result.FixName} completed.";
                ToastService.Instance.Show(result.FixName, result.NeedsReboot ? "Done — reboot recommended." : "Done.");
            }
            else
            {
                StatusMessage = $"{result.FixName} did not complete — see the output for details.";
            }
            Log.Information("SystemFix: {Fix} success={Success}", result.FixName, result.Success);
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsFixRunning = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void OpenAutologin()
    {
        // Auto-logon is configured through the built-in netplwiz dialog, which stores the
        // credential securely (LSA secret) — SysManager never writes a plaintext password.
        try
        {
            Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("netplwiz.exe")) { UseShellExecute = true })?.Dispose();
            StatusMessage = "Opened User Accounts (netplwiz). Untick \"Users must enter a user name and password\" to enable auto sign-in.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("SystemFix: could not open netplwiz: {Error}", ex.Message);
            StatusMessage = "Couldn't open the User Accounts dialog.";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        _sfcCts?.Cancel();
        _dismCts?.Cancel();
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.LineReceived -= OnLine;
            _runner.LineReceived -= OnLine;
            _cts?.Cancel();
            _cts?.Dispose();
            _sfcCts?.Cancel();
            _sfcCts?.Dispose();
            _dismCts?.Cancel();
            _dismCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // SFC reports progress as a whole-number percentage, e.g. "50 %".
    [GeneratedRegex(@"(\d+)\s*%")]
    private static partial Regex SfcPercentRegex();

    // DISM reports progress as a decimal percentage, e.g. "50.0%".
    [GeneratedRegex(@"([\d.]+)%")]
    private static partial Regex DismPercentRegex();
}
