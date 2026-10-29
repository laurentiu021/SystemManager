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
/// ones (Windows Update, WinGet), two desktop fixes (restart Explorer, rebuild the icon cache), plus
/// a secure shortcut to the built-in auto-logon dialog. Each repair confirms first, writes its output
/// to the shared console, and reports its result honestly. The Windows repairs need administrator
/// rights, and the tab shows the standard elevation banner for them; the desktop fixes run as the user.
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
public sealed partial class SystemFixesViewModel : ElevatedViewModelBase
{
    // Escape stops nothing here, so there is no EscapeCancel: every run on this tab repairs or changes Windows, and
    // Escape is a bare keypress. It stopped SFC, DISM and the scripted repairs half-way (#2834). The Cancel button
    // still stops one, for a stop that is meant.

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

    /// <summary>
    /// The sentences SFC ends a scan with, by their first lines, as this Windows prints them. A seam: the default reads
    /// <c>sfc.exe</c>'s message table, and a unit test gives the English ones instead.
    /// </summary>
    internal Func<IReadOnlyDictionary<string, SfcOutcome>> SfcSentences { get; init; } =
        static () => SfcOutput.Sentences;

    /// <summary>Shared, capped, thread-safe console — same control the other repair/scan tabs use.</summary>
    public ConsoleViewModel Console { get; } = new();

    public SystemFixesViewModel(SystemFixService service, IPowerShellRunner runner)
    {
        _service = service;
        _runner = runner;
        StatusMessage = "Pick a repair. Each asks for confirmation before it runs.";
        _service.LineReceived += OnLine;
        _runner.LineReceived += OnLine;
        PropertyChanged += OnVmPropertyChanged;
    }

    // IsElevated now lives on ElevatedViewModelBase, so its change notification can no longer reach
    // NotifyFixCommands through an OnIsElevatedChanged partial hook (the toolkit generates that hook's
    // declaration in the SAME partial class as the field, which is the base — a derived class cannot
    // implement a base class's partial method). PropertyChanged is the base's own notification channel.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsElevated)) NotifyFixCommands();
    }

    /// <summary>
    /// Every repair is exclusive: they all stream into the one console and drive the one progress
    /// bar, so a second concurrent repair would interleave its output with the first's.
    /// </summary>
    private bool CanRunFix => !IsAnyRunning && IsElevated;

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
        StatusMessage = $"{what} needs administrator rights — use \"Run as administrator\" above.";
        return false;
    }

    private void NotifyFixCommands()
    {
        RunSfcCommand.NotifyCanExecuteChanged();
        RunDismCommand.NotifyCanExecuteChanged();
        ResetWindowsUpdateCommand.NotifyCanExecuteChanged();
        ReinstallWinGetCommand.NotifyCanExecuteChanged();
        RestartExplorerCommand.NotifyCanExecuteChanged();
        RebuildIconCacheCommand.NotifyCanExecuteChanged();
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
        // The tab promises a confirmation before every fix, and this one changes system files (#2505).
        if (!DialogService.Instance.Confirm(
                "This checks every protected Windows system file and replaces the damaged ones with a known-good "
                + "copy. It changes system files and takes 5 to 15 minutes. You can keep using the app while it "
                + "runs, and some repairs finish only after a restart.\n\nContinue?",
                "Run SFC?"))
        {
            StatusMessage = "Cancelled.";
            return;
        }

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
        // Read off the UI thread while SFC runs.
        var sentences = Task.Run(SfcSentences);
        var captured = new List<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
            if (SfcOutput.ProgressIn(l.Text) is { } pct)
            {
                Progress = pct;
                SfcEtaText = _sfcEta.Update(pct);
                IsProgressIndeterminate = false;
            }
        }
        _runner.LineReceived += Collect;
        try
        {
            // SFC writes UTF-16, not the OEM code page the other tools write (#2835).
            var exit = await _runner.RunProcessAsync("sfc.exe", "/scannow", _sfcCts.Token, SfcOutput.Encoding);
            var (verdict, color) = ParseSfcResult(captured, exit, await sentences);
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
    /// The verdict on SFC's output: the worst of the outcomes its lines name, found by the first lines of SFC's own
    /// messages in <paramref name="sentences"/>, so in whatever language Windows printed them (#2835).
    /// </summary>
    /// <remarks>
    /// Output that names none of them is not called a success. Exit code 0 says SFC ran, not what it found, and the
    /// English phrases this matched before ended a scan that could not repair files in green on any other language.
    /// </remarks>
    internal static (string Verdict, string ColorHex) ParseSfcResult(
        IReadOnlyList<string> lines, int exitCode, IReadOnlyDictionary<string, SfcOutcome> sentences)
    {
        var seen = SfcOutput.OutcomesIn(lines, sentences);
        foreach (var (outcome, verdict, color) in SfcVerdicts)
        {
            if (!seen.Contains(outcome)) continue;
            return outcome == SfcOutcome.Repaired && seen.Contains(SfcOutcome.TakesEffectAfterRestart)
                ? (verdict + " Restart Windows to finish the repair.", color)
                : (verdict, color);
        }

        return exitCode == 0
            ? ("Scan finished. Its result is in the console output.", StatusColors.Neutral)
            : ($"Scan finished with exit code {exitCode}. Check the console output for details.", StatusColors.Warning);
    }

    /// <summary>The verdict on each way SFC can end a scan, worst first, in <see cref="SfcOutcome"/> order.</summary>
    internal static readonly IReadOnlyList<(SfcOutcome Outcome, string Verdict, string ColorHex)> SfcVerdicts =
    [
        (SfcOutcome.CouldNotPerform,
            "SFC could not run. Try rebooting into Safe Mode or running DISM first.", StatusColors.Bad),
        (SfcOutcome.ServiceDidNotStart,
            "SFC could not start its repair service. Restart Windows, then run SFC again.", StatusColors.Bad),
        (SfcOutcome.UnableToFix,
            "Corrupted files found but SFC could not repair them. "
            + "Try running DISM /RestoreHealth first, then SFC again.", StatusColors.Bad),
        (SfcOutcome.CannotGuarantee,
            "SFC found no damaged files, but could not check them all because Windows' repair source is damaged. "
            + "Run DISM /RestoreHealth, then SFC again.", StatusColors.Warning),
        (SfcOutcome.AnotherOperationRunning,
            "Another repair or update is running. Wait for it to finish, then run SFC again.", StatusColors.Warning),
        (SfcOutcome.RepairPending,
            "A repair is waiting for a restart. Restart Windows, then run SFC again.", StatusColors.Warning),
        (SfcOutcome.MaximumQueued,
            "SFC queued as many repairs as it can at once. Restart Windows to finish them, then run SFC again.",
            StatusColors.Warning),
        (SfcOutcome.MaximumRepaired,
            "SFC repaired as many files as it can at once. Run SFC again to repair the rest.", StatusColors.Warning),
        (SfcOutcome.Repaired, "Corrupted files were found and successfully repaired.", StatusColors.Warning),
        (SfcOutcome.TakesEffectAfterRestart,
            "SFC's repairs take effect after a restart. Restart Windows to finish them.", StatusColors.Warning),
        (SfcOutcome.NoViolations, "No integrity violations found — your system files are healthy.", StatusColors.Good),
    ];

    /// <summary>
    /// Runs <c>DISM /Online /Cleanup-Image /RestoreHealth</c>: repairs the component store SFC
    /// draws its known-good copies from, using Windows Update as the source.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunDismAsync()
    {
        if (IsDismRunning) return;
        if (!RequireElevation("Repairing the component store")) return;
        if (!DialogService.Instance.Confirm(
                "This checks the store Windows keeps its known-good copies of system files in, and replaces the "
                + "damaged ones with copies downloaded from Windows Update, so it needs an internet connection. It "
                + "changes system files and takes 10 to 30 minutes. You can keep using the app while it runs."
                + "\n\nContinue?",
                "Run DISM?"))
        {
            StatusMessage = "Cancelled.";
            return;
        }

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
            var exit = await _runner.RunProcessAsync("DISM.exe", "/Online /English /Cleanup-Image /RestoreHealth", _dismCts.Token, PowerShellRunner.OemEncoding);
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
    /// <remarks>
    /// DISM ends a repair and a run that found nothing to repair with the same sentence, "The restore operation
    /// completed successfully.", so the verdict says the store is healthy now and to run SFC, the next step either way.
    /// It said no repairs were needed, and the branch for a "corruption was repaired" sentence never ran: DISM has no
    /// such sentence (#2835). The run passes <c>/English</c>, so these are DISM's sentences on any language.
    /// </remarks>
    internal static (string Verdict, string ColorHex) ParseDismResult(IReadOnlyList<string> lines, int exitCode)
    {
        var all = string.Join(" ", lines);

        if (all.Contains("The restore operation completed successfully", StringComparison.OrdinalIgnoreCase))
            return ("Windows' repair source is healthy now. Run SFC next to repair any damaged Windows files.",
                StatusColors.Good);

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
        // Names the two things the reset does beyond its headline: it stops Windows Installer, and it keeps the
        // renamed folders, a new pair every time (#2505).
        "This stops the Windows Update services and Windows Installer, renames their cache folders " +
        "(SoftwareDistribution and catroot2) so Windows rebuilds them, and restarts the services. Pending " +
        "updates will re-download. A reboot is recommended afterwards.\n\n" +
        "Stopping Windows Installer interrupts any installation running in another program right now, so let " +
        "it finish first.\n\n" +
        "The old folders are kept beside the new ones, named with .old and the date, and every reset keeps " +
        "another copy. They can take several GB, and SysManager does not remove them.\n\nContinue?",
        ct => _service.ResetWindowsUpdateAsync(ct),
        // It force-stops wuauserv, cryptSvc, bits and msiserver and renames the update caches, so it must not run
        // during a Windows Update install, an SFC or DISM repair, or a feature change (#2484).
        systemLock: "Windows Update reset",
        // msiserver is Windows Installer: stopping it cuts off an app install, upgrade or uninstall that App Updates,
        // Bulk Installer, Uninstaller or Update All Apps is running (#2553).
        stopsWindowsInstaller: true);

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private Task ReinstallWinGetAsync() => RunFixAsync(
        "Reinstall WinGet?",
        "This re-registers the Windows Package Manager (App Installer) for your account, " +
        "which fixes most cases where app installs or uninstalls fail. No reboot needed.\n\nContinue?",
        ct => _service.ReinstallWinGetAsync(ct));

    /// <summary>
    /// Runs one of the scripted repairs: elevation, then the confirmation, then the lock when the fix names one,
    /// then the script, reported in the status line.
    /// </summary>
    /// <param name="systemLock">
    /// The name to hold the system-modification lock under, or null for a fix that conflicts with nothing that
    /// takes it. Reinstalling WinGet re-registers a package for the user and needs no lock.
    /// </param>
    /// <param name="stopsWindowsInstaller">
    /// True for a fix that stops the Windows Installer service. It then also holds the install lock, under the same
    /// name, so it neither starts under an install SysManager is running nor lets one start until it is done.
    /// </param>
    private async Task RunFixAsync(string title, string message, Func<CancellationToken, Task<SystemFixResult>> fix,
                                   string? systemLock = null, bool stopsWindowsInstaller = false)
    {
        if (IsFixRunning) return;
        if (!RequireElevation(title.TrimEnd('?'))) return;
        if (!DialogService.Instance.Confirm(message, title))
        {
            StatusMessage = "Cancelled.";
            return;
        }

        using var opLock = systemLock is null
            ? null
            : OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, systemLock);
        if (systemLock is not null && opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        using var installLock = stopsWindowsInstaller
            ? OperationLockService.Instance.TryAcquire(OperationCategory.Install, systemLock ?? title.TrimEnd('?'))
            : null;
        if (stopsWindowsInstaller && installLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install)} is already running.";
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
                // The script's own reason, which the console never received: it arrives as the run's exception (#2875).
                var reason = result.FailureReason?.Trim();
                if (string.IsNullOrEmpty(reason))
                {
                    StatusMessage = $"{result.FixName} did not complete.";
                }
                else
                {
                    Console.Append(PowerShellLine.Err(reason));
                    StatusMessage = $"{result.FixName} did not complete: {reason.Split('\n', 2)[0].TrimEnd()}";
                }
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

    // ---------- the desktop and taskbar (no administrator rights needed) ----------

    /// <summary>
    /// The two shell fixes run as the user: Explorer is the user's own process and the icon caches live
    /// under their profile. So they gate on <see cref="IsAnyRunning"/> alone, NOT on
    /// <see cref="CanRunFix"/> — requiring elevation for them would put the app's most common,
    /// cheapest fixes behind a UAC prompt they do not need.
    /// </summary>
    private bool CanRunShellFix => !IsAnyRunning;

    /// <summary>
    /// Ends every Explorer instance and brings one back — the fix for a frozen taskbar or a desktop
    /// that has vanished.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunShellFix))]
    private async Task RestartExplorerAsync()
    {
        if (IsFixRunning) return;
        if (!DialogService.Instance.Confirm(
                "Restart Windows Explorer now?\n\nAll open File Explorer windows will close. Your files are "
                + "not affected, and the taskbar and desktop come back on their own within a few seconds.",
                "Restart Explorer"))
        {
            StatusMessage = "Cancelled.";
            return;
        }

        await RunShellFixAsync("Explorer restart", () =>
        {
            ExplorerShell.Restart();
            return "Explorer restarted — the taskbar and desktop have been rebuilt.";
        });
    }

    /// <summary>
    /// Deletes the icon and thumbnail caches with the shell stopped, then brings it back so Windows
    /// rebuilds them — the fix for blank or wrong icons and thumbnails.
    /// </summary>
    /// <remarks>
    /// Explorer must be stopped for this to work at all, which is what makes it a distinct operation
    /// rather than a duplicate of Deep Cleanup's cache category. Measured on a live desktop, 13 of 30
    /// cache files were held open — and the locked ones are precisely the ones caching the wrong icons,
    /// so a delete with the shell running removes the harmless half.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunShellFix))]
    private async Task RebuildIconCacheAsync()
    {
        if (IsFixRunning) return;
        if (!DialogService.Instance.Confirm(
                "Rebuild the icon and thumbnail cache?\n\nWindows keeps a cache of every icon and picture "
                + "preview, and clearing it fixes icons that show up blank or wrong. Explorer has to close "
                + "for this, so all open File Explorer windows will close and the desktop will come back "
                + "within a few seconds. Nothing of yours is deleted — Windows rebuilds the cache as you "
                + "browse, so the first few folders may open a little slower.",
                "Rebuild icon cache"))
        {
            StatusMessage = "Cancelled.";
            return;
        }

        await RunShellFixAsync("Icon cache rebuild", () =>
        {
            // Stop -> delete -> start, and the start happens even if the delete throws: leaving the
            // user without a desktop would be far worse than a cache that did not clear.
            ExplorerShell.Stop();
            ExplorerShell.CacheSweep sweep;
            try
            {
                sweep = ExplorerShell.DeleteCacheFiles();
            }
            finally
            {
                ExplorerShell.Start();
            }

            if (sweep.Deleted == 0 && sweep.Failed == 0)
                return "No cache files were there to clear — Windows had already rebuilt them.";

            var freed = FormatHelper.FormatSize(sweep.BytesFreed);
            return sweep.Failed == 0
                ? $"Cleared {sweep.Deleted} cache file(s), {freed}. Icons rebuild as you browse."
                : $"Cleared {sweep.Deleted} cache file(s), {freed}. {sweep.Failed} were still in use and "
                  + "were left alone — running this again usually gets them.";
        });
    }

    /// <summary>
    /// Shared body for the two shell fixes: cross-tab lock, off the UI thread, honest status.
    /// </summary>
    /// <remarks>
    /// The <see cref="OperationCategory.Shell"/> lock is what stops this racing the Right-Click Menu tab's
    /// restart. Two overlapping restarts can leave the user with no desktop at all, and
    /// <see cref="IsAnyRunning"/> is per-view-model so it cannot see the other tab.
    /// </remarks>
    private async Task RunShellFixAsync(string name, Func<string> work)
    {
        using var shellLock = OperationLockService.Instance.TryAcquire(OperationCategory.Shell, name);
        if (shellLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Shell)} is already running.";
            return;
        }

        IsFixRunning = true;
        IsProgressIndeterminate = true;
        StatusMessage = $"{name} in progress...";
        try
        {
            // Off the UI thread: killing and relaunching the shell is synchronous and takes seconds.
            var outcome = await Task.Run(work);
            StatusMessage = outcome;
            Console.Append(PowerShellLine.Output(outcome));
            ToastService.Instance.Show(name, outcome);
            Log.Information("SystemFix: {Fix} — {Outcome}", name, outcome);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            StatusMessage = $"{name} failed: {ex.Message}";
            Log.Warning("SystemFix: {Fix} failed: {Error}", name, ex.Message);
        }
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

    // DISM reports progress as a decimal percentage, e.g. "50.0%".
    [GeneratedRegex(@"([\d.]+)%")]
    private static partial Regex DismPercentRegex();
}
