// SysManager · UninstallerViewModel — uninstall apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Uninstaller tab — lists installed apps, filter, select, uninstall.
/// </summary>
public sealed partial class UninstallerViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly UninstallerService _service;
    private readonly ILeftoverService _leftovers;
    private readonly EtaCalculator _uninstallEta = new();
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<InstalledApp> AllApps { get; } = new();
    public BulkObservableCollection<InstalledApp> FilteredApps { get; } = new();

    /// <summary>What the last uninstall run left behind, one group per app, for the "Left behind" card (#1527).</summary>
    public BulkObservableCollection<LeftoverGroup> LeftoverGroups { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _summary = "Click Scan to list installed applications.";
    [ObservableProperty] private string _uninstallEtaText = string.Empty;
    [ObservableProperty] private bool _isElevated;

    /// <summary>Whether the "Left behind" card shows. It appears after a run that removed an app, and on its own when
    /// SysManager runs as administrator with items an earlier session could not remove.</summary>
    [ObservableProperty] private bool _showLeftovers;

    /// <summary>The card's opening sentence.</summary>
    [ObservableProperty] private string _leftoverIntro = "";

    /// <summary>What is ticked and where it goes, beside the card's button.</summary>
    [ObservableProperty] private string _leftoverSelection = "";

    /// <summary>Whether the card lists anything to remove, which decides whether its button row shows.</summary>
    [ObservableProperty] private bool _hasLeftoverItems;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public UninstallerViewModel(UninstallerService service, ILeftoverService leftovers)
        : this(service, leftovers, AdminHelper.IsElevated) { }

    /// <summary>Test seam: the same view-model with the elevation probe supplied.</summary>
    /// <param name="service">Lists and uninstalls apps.</param>
    /// <param name="leftovers">Finds and removes what an uninstall left behind.</param>
    /// <param name="isElevated">
    /// Whether SysManager runs as administrator. Injected so a test decides whether the card offers what an earlier
    /// session left for an administrator, rather than inheriting the elevation of whatever runs the suite.
    /// </param>
    internal UninstallerViewModel(UninstallerService service, ILeftoverService leftovers, Func<bool> isElevated)
    {
        _service = service;
        _leftovers = leftovers ?? throw new ArgumentNullException(nameof(leftovers));
        // Scan and UninstallSelected both recreate the shared _cts; without this gate a
        // second command could dispose the CTS the first is still awaiting
        // (ObjectDisposedException). Re-evaluate both commands' CanExecute when IsBusy flips.
        PropertyChanged += OnVmPropertyChanged;
        IsElevated = (isElevated ?? throw new ArgumentNullException(nameof(isElevated)))();
        if (IsElevated) InitializeAsync(OfferPendingLeftoversAsync);
    }

    /// <summary>
    /// Gate for the long-running commands. Scan and UninstallSelected share <see cref="_cts"/>
    /// and each recreates it, so disabling both while one runs prevents a second command from
    /// disposing the CTS mid-flight. Cancel is intentionally NOT gated. Mirrors the App Updates
    /// and Windows Update tabs.
    /// </summary>
    private bool NotBusy => !IsBusy;

    /// <summary>
    /// True once a scan has listed at least one app. Select-all / deselect / uninstall
    /// act on the list, so they stay disabled on an empty (unscanned) list rather than
    /// appearing operable with nothing to act on.
    /// </summary>
    private bool HasApps => AppCount > 0;

    /// <summary>
    /// Uninstall needs a populated list, no active command, and an unelevated
    /// SysManager process. The selected package owns any UAC request it needs.
    /// </summary>
    private bool CanUninstall => NotBusy && HasApps && !IsElevated;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    // AppCount is refreshed by ApplyFilter; re-evaluate the list-dependent commands
    // whenever it changes so their enabled state tracks the (un)populated list.
    partial void OnAppCountChanged(int value)
    {
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        DeselectAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsElevatedChanged(bool value) =>
        UninstallSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying winget list…";
        FilteredApps.Clear();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListInstalledAsync(_cts.Token);
            foreach (var app in list)
                app.Icon ??= IconExtractorService.FallbackIcon;
            // Keep the user's ticks across the rescan. These rows arrive UNSELECTED, so a rescan cleared the
            // selection rather than reversing it — the Uninstall button simply stopped doing anything until
            // every app was ticked again. Less dangerous than the tabs whose rows arrive pre-selected, but
            // the same defect, and RefreshOnF5 is ScanCommand (#2304).
            //
            // Keyed on id AND name: this list is not winget-only, and an entry discovered through the
            // registry can have an empty id — keying on that alone would collapse every such app into one.
            Helpers.SelectionCarry.Apply(AllApps, list, a => (a.Id, a.Name));
            AllApps.ReplaceWith(list);

            ApplyFilter();
            StatusMessage = $"Found {AllApps.Count} installed applications.";
            ToastService.Instance.Show("Uninstaller scan complete", $"Found {AllApps.Count} installed applications");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        // winget.exe missing (App Installer absent / execution alias off) throws
        // Win32Exception from Process.Start. Scan is the tab's first action, so without
        // this the raw OS-error dialog pops immediately. Reuse the AppUpdates message so
        // both winget tabs speak with one voice.
        catch (System.ComponentModel.Win32Exception)
        {
            StatusMessage = AppUpdatesViewModel.WingetUnavailableMessage;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallSelectedAsync()
    {
        var toRemove = FilteredApps.Where(a => a.IsSelected).ToList();
        if (toRemove.Count == 0)
        {
            StatusMessage = "No apps selected.";
            return;
        }

        var names = string.Join("\n", toRemove.Take(10).Select(a => $"  • {a.Name}"));
        if (toRemove.Count > 10)
            names += $"\n  … and {toRemove.Count - 10} more";

        if (!DialogService.Instance.Confirm(
            $"You are about to uninstall {toRemove.Count} application(s):\n\n{names}\n\nThis cannot be undone. Continue?",
            "Confirm uninstall")) return;

        // Windows Installer runs one installation at a time process-wide, so an MSI-based app uninstalled
        // here while App Updates or Bulk Installer is also mid-run can fail with exit code 1618. The same
        // lock stops any two of the three from overlapping (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Install, "Uninstaller");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install)} is already running.";
            return;
        }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int completed = 0;
        int removed = 0;
        int failed = 0;
        int stillOpen = 0;
        int restartRequired = 0;
        bool cancellationRequested = false;
        UninstallEtaText = string.Empty;
        _uninstallEta.Reset();
        // Read before any uninstaller runs: some delete their own uninstall entry first, and the install location
        // the leftover search starts from comes from that entry (#1527).
        var probes = toRemove.ToDictionary(a => a, UninstallProbe.Of);
        var removedApps = new List<InstalledApp>();

        try
        {
            foreach (var app in toRemove)
            {
                if (_cts.IsCancellationRequested)
                {
                    cancellationRequested = true;
                    break;
                }

                app.Status = "Uninstalling...";
                StatusMessage = $"Uninstalling {app.Name} ({completed + 1}/{toRemove.Count})...";
                Progress = (int)(completed * 100.0 / toRemove.Count);
                UninstallEtaText = _uninstallEta.Update(Progress);
                var currentCompleted = false;

                try
                {
                    var local = string.IsNullOrWhiteSpace(app.Source)
                        && !string.IsNullOrWhiteSpace(app.UninstallString);
                    var code = local
                        ? await _service.UninstallLocalAsync(app, _cts.Token)
                        : await _service.UninstallAsync(app.Id, _cts.Token);

                    currentCompleted = true;
                    if (IsSuccessfulUninstallExitCode(code) && _service.IsStillRegistered(app))
                    {
                        // The exit code belongs to the process that was launched, which can return before
                        // anything is removed: NSIS uninstallers hand over to a copy of themselves and exit at
                        // once (#2448). Through winget too — it waits on the process it started and never looks
                        // at the uninstall list again (#2469). Windows still lists the app, so it is not counted
                        // or taken off the list.
                        app.Status = StillOpenStatus;
                        stillOpen++;
                    }
                    else if (IsSuccessfulUninstallExitCode(code))
                    {
                        var needsRestart = RequiresRestartAfterUninstall(code);
                        app.Status = needsRestart ? "Removed - restart required" : "Removed";
                        removed++;
                        removedApps.Add(app);
                        if (needsRestart)
                            restartRequired++;
                        AllApps.Remove(app);
                        FilteredApps.Remove(app);
                    }
                    else
                    {
                        app.Status = DescribeUninstallFailure(code, app.Name);
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    app.Status = "Cancelled";
                    cancellationRequested = true;
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = !_cts.IsCancellationRequested;
                }
                // A failed uninstaller launch (missing/blocked exe) must not abort the
                // whole batch - record it on the row and continue with the next app.
                catch (System.ComponentModel.Win32Exception ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }
                catch (System.IO.IOException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }
                // An unparseable package Id (e.g. an ARP GUID) throws ArgumentException from
                // UninstallAsync before any process runs; record it and continue the batch.
                catch (ArgumentException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }

                if (currentCompleted)
                    completed++;

                Progress = (int)(completed * 100.0 / toRemove.Count);
                UninstallEtaText = _uninstallEta.Update(Progress);

                if (_cts.IsCancellationRequested && completed < toRemove.Count)
                {
                    cancellationRequested = true;
                    break;
                }
            }

            UninstallEtaText = string.Empty;
            var restartMessage = restartRequired switch
            {
                0 => string.Empty,
                1 => " Restart required for 1 app.",
                _ => $" Restart required for {restartRequired} apps."
            };
            var stillOpenMessage = DescribeStillOpen(stillOpen);
            if (cancellationRequested)
            {
                StatusMessage = $"Uninstall cancelled after {completed}/{toRemove.Count} completed. Removed {removed}; failed {failed}.{restartMessage}{stillOpenMessage}";

                Log.Information(
                    "Uninstall batch cancelled: {Completed}/{Total} completed, {Removed} removed, {Failed} failed, {StillOpen} still listed, {RestartRequired} need restart",
                    completed,
                    toRemove.Count,
                    removed,
                    failed,
                    stillOpen,
                    restartRequired);
            }
            else if (failed > 0)
            {
                Progress = 100;
                StatusMessage = $"Uninstall finished with errors. Removed {removed}; failed {failed}.{restartMessage}{stillOpenMessage}";

                Log.Warning(
                    "Uninstall batch finished with errors: {Removed} removed, {Failed} failed, {StillOpen} still listed, {RestartRequired} need restart, {Total} total",
                    removed,
                    failed,
                    stillOpen,
                    restartRequired,
                    toRemove.Count);
            }
            else
            {
                Progress = 100;
                StatusMessage = $"Completed {removed}/{toRemove.Count} uninstalls.{restartMessage}{stillOpenMessage}";
                ToastService.Instance.Show(
                    stillOpen > 0 ? "Uninstaller still running" : "Uninstall complete",
                    $"Completed {removed}/{toRemove.Count} uninstalls.{restartMessage}{stillOpenMessage}");
                Log.Information(
                    "Uninstall batch completed: {Removed}/{Total}, {StillOpen} still listed, {RestartRequired} need restart",
                    removed,
                    toRemove.Count,
                    stillOpen,
                    restartRequired);
            }

            // Logged once after all three branches, so a cancelled or partly-failed batch is recorded
            // as what it actually was rather than not at all. Counts only — the app NAMES are omitted
            // deliberately: activity.json is plain text under %LocalAppData%, and which software
            // someone removed is more revealing than how many.
            if (removed > 0)
            {
                ActivityLogService.Instance.Log("Uninstaller",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Uninstalled {removed:N0} app{(removed == 1 ? "" : "s")}") +
                    (failed > 0 ? $" ({failed} failed)" : string.Empty) +
                    (cancellationRequested ? " — cancelled partway" : string.Empty));

                await ShowLeftoversAsync([.. removedApps.Select(a => probes[a])]);
            }
        }
        finally
        {
            IsBusy = false;
            UninstallEtaText = string.Empty;
            ApplyFilter();
        }
    }

    // ── What an uninstall left behind (#1527) ─────────────────────────────────

    /// <summary>
    /// Searches for what the apps just uninstalled left behind and shows it on the "Left behind" card.
    /// </summary>
    /// <remarks>
    /// The card appears after every run that removed an app, because the question comes right after uninstalling and
    /// nobody goes looking for a button. Items that need administrator rights are remembered, since uninstalling runs
    /// without them, and offered again when this tab is opened in a session that runs as administrator.
    /// <para>The batch's own summary is put back on the status line afterwards: the search is a footnote to the
    /// uninstall, not its result.</para>
    /// </remarks>
    private async Task ShowLeftoversAsync(IReadOnlyList<UninstallProbe> uninstalled)
    {
        var summary = StatusMessage;
        StatusMessage = "Looking for what the uninstall left behind…";
        var stillInstalled = AllApps.Select(UninstallProbe.Of).ToList();
        var groups = new List<LeftoverGroup>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var probe in uninstalled)
            {
                LeftoverGroup? group;
                try { group = await _leftovers.FindAsync(probe, stillInstalled).ConfigureAwait(true); }
                catch (System.IO.IOException ex) { Log.Warning("Leftover search for an app failed: {Error}", ex.Message); continue; }
                catch (UnauthorizedAccessException ex) { Log.Warning("Leftover search for an app denied: {Error}", ex.Message); continue; }
                if (group is null) continue;

                // Two apps of one publisher uninstalled together find the same publisher folder; it is listed once,
                // under the first, so ticking it in one place is the only tick there is.
                foreach (var duplicate in group.Items.Where(i => !listed.Add(i.Location)).ToList())
                    group.Items.Remove(duplicate);
                groups.Add(group);
            }

            _leftovers.RememberPending(groups);
            ShowGroups([.. groups.Where(g => g.Items.Count > 0 || g.NotOffered.Count > 0)]);
            LeftoverIntro = DescribeFindings(uninstalled.Count, LeftoverGroups);
            ShowLeftovers = true;
        }
        finally
        {
            StatusMessage = summary;
        }
    }

    /// <summary>The card's opening sentence after an uninstall, for what was found.</summary>
    internal static string DescribeFindings(int uninstalled, IReadOnlyCollection<LeftoverGroup> groups)
    {
        var one = uninstalled == 1;
        if (groups.Any(g => g.Items.Count > 0))
        {
            return $"{uninstalled} app{(one ? " was" : "s were")} uninstalled. These were still on the PC afterwards. "
                   + "Ticked items are certain; the rest are guesses — tick them only if you know they are not used by "
                   + "something else.";
        }

        var whose = one ? "the app you uninstalled" : $"the {uninstalled} apps you uninstalled";
        return groups.Any(g => g.NotOffered.Count > 0)
            ? $"Nothing to remove. A few things named after {whose} were found, but they are in places SysManager never "
              + "touches, so they are only listed below."
            : $"Nothing left behind. SysManager found no folder or setting of {whose}.";
    }

    /// <summary>
    /// Offers, as administrator, what an earlier session could not remove without administrator rights.
    /// </summary>
    private async Task OfferPendingLeftoversAsync()
    {
        IReadOnlyList<LeftoverGroup> pending;
        try { pending = await Task.Run(_leftovers.LoadPending).ConfigureAwait(true) ?? []; }
        catch (System.IO.IOException ex) { Log.Warning("Pending leftovers could not be read: {Error}", ex.Message); return; }
        catch (UnauthorizedAccessException ex) { Log.Warning("Pending leftovers denied: {Error}", ex.Message); return; }

        if (pending.Count == 0) return;
        ShowGroups(pending);
        LeftoverIntro = "Apps you uninstalled earlier left these behind, and removing them needs administrator rights, "
                        + "which SysManager has now. Nothing is ticked: tick what you want removed.";
        ShowLeftovers = true;
    }

    private void ShowGroups(IReadOnlyList<LeftoverGroup> groups)
    {
        foreach (var item in LeftoverGroups.SelectMany(g => g.Items)) item.PropertyChanged -= OnLeftoverChanged;
        LeftoverGroups.ReplaceWith(groups);
        foreach (var item in LeftoverGroups.SelectMany(g => g.Items)) item.PropertyChanged += OnLeftoverChanged;
        UpdateLeftoverSelection();
    }

    private void OnLeftoverChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverItem.IsSelected)) UpdateLeftoverSelection();
    }

    private IReadOnlyList<LeftoverItem> TickedLeftovers =>
        [.. LeftoverGroups.SelectMany(g => g.Items).Where(i => i.IsSelected && i.CanSelect)];

    private void UpdateLeftoverSelection()
    {
        HasLeftoverItems = LeftoverGroups.Any(g => g.Items.Count > 0);
        LeftoverSelection = DescribeSelection(TickedLeftovers);
        RemoveLeftoversCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The line beside the card's button: how much is ticked and where it goes.</summary>
    internal static string DescribeSelection(IReadOnlyList<LeftoverItem> ticked)
    {
        if (ticked.Count == 0) return "Nothing ticked.";
        var where = ticked.All(i => i.Kind == LeftoverKind.Folder)
            ? "goes to the Recycle Bin, so you can put it back"
            : ticked.All(i => i.Kind == LeftoverKind.RegistryKey)
                ? "saved to a file first, then deleted"
                : "folders go to the Recycle Bin, and registry keys are saved to a file first";
        return $"{ticked.Count} ticked · {FormatHelper.FormatSize(LeftoverFinder.DistinctBytes(ticked))} · {where}";
    }

    private bool CanRemoveLeftovers => NotBusy && TickedLeftovers.Count > 0;

    /// <summary>
    /// Sends the ticked folders to the Recycle Bin and deletes the ticked keys after saving each to a file, once the
    /// user has confirmed the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveLeftovers))]
    private async Task RemoveLeftoversAsync()
    {
        var ticked = TickedLeftovers;
        if (ticked.Count == 0) return;
        if (!DialogService.Instance.Confirm(DescribeRemoval(ticked), "Remove Leftovers — Confirm")) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var result = await _leftovers.RemoveAsync(ticked).ConfigureAwait(true);

            var gone = result.Removed.ToHashSet();
            foreach (var group in LeftoverGroups)
            {
                foreach (var item in group.Items.Where(gone.Contains).ToList())
                {
                    item.PropertyChanged -= OnLeftoverChanged;
                    group.Items.Remove(item);
                }
            }
            _leftovers.ForgetPending([.. result.Removed.Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Location)]);
            ShowGroups([.. LeftoverGroups.Where(g => g.Items.Count > 0)]);
            if (LeftoverGroups.Count == 0) ShowLeftovers = false;

            StatusMessage = DescribeRemovalResult(result);
            var n = result.Removed.Count;
            if (n > 0)
            {
                // Counts only, as for the uninstalls themselves: which software someone removed is not logged.
                ActivityLogService.Instance.Log("Uninstaller", DescribeRemovalForTheLog(result));
            }
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>The status line after a removal: what went, and the first thing that did not, with why.</summary>
    internal static string DescribeRemovalResult(LeftoverRemoval result)
    {
        var n = result.Removed.Count;
        var failed = result.Errors.Count;
        if (n == 0)
            return failed == 0 ? "Nothing was removed." : $"Nothing was removed. {result.Errors[0]}";

        var done = $"Removed {n} leftover item{(n == 1 ? "" : "s")} ({FormatHelper.FormatSize(result.BytesFreed)}).";
        return failed == 0 ? done : $"{done} {failed} could not be removed. {result.Errors[0]}";
    }

    /// <summary>The activity-log line for a removal: how many, how much, and where each kind went. No names.</summary>
    internal static string DescribeRemovalForTheLog(LeftoverRemoval result)
    {
        var n = result.Removed.Count;
        var where = string.Join(", and ", new[]
        {
            result.Removed.Any(i => i.Kind == LeftoverKind.Folder) ? "folders went to the Recycle Bin" : null,
            result.Removed.Any(i => i.Kind == LeftoverKind.RegistryKey) ? "registry keys were saved to a file first" : null,
        }.OfType<string>());
        return $"Removed {n} leftover item{(n == 1 ? "" : "s")} of uninstalled apps "
               + $"({FormatHelper.FormatSize(result.BytesFreed)}); {where}";
    }

    /// <summary>Hides the card. As administrator, what it showed is also forgotten: it could have been removed.</summary>
    [RelayCommand]
    private void DismissLeftovers()
    {
        if (IsElevated)
        {
            _leftovers.ForgetPending([.. LeftoverGroups.SelectMany(g => g.Items)
                .Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Location)]);
        }
        ShowGroups([]);
        ShowLeftovers = false;
    }

    /// <summary>The confirmation's question: about the Recycle Bin when only folders are ticked.</summary>
    internal static string ConfirmRemovalQuestion(IReadOnlyList<LeftoverItem> ticked) =>
        ticked.All(i => i.Kind == LeftoverKind.Folder)
            ? $"Send {ticked.Count} item{(ticked.Count == 1 ? "" : "s")} to the Recycle Bin?"
            : $"Remove {ticked.Count} leftover item{(ticked.Count == 1 ? "" : "s")}?";

    /// <summary>What the confirmation lists and promises. The wording IS the safeguard here, so it is tested.</summary>
    internal static string DescribeRemoval(IReadOnlyList<LeftoverItem> ticked)
    {
        var lines = string.Join("\n", ticked.Take(10).Select(i =>
            i.Kind == LeftoverKind.Folder ? $"  • {i.DisplayLocation} — {i.SizeDisplay}" : $"  • {i.DisplayLocation}"));
        if (ticked.Count > 10) lines += $"\n  … and {ticked.Count - 10} more";

        var folders = ticked.Any(i => i.Kind == LeftoverKind.Folder)
            ? "Folders go to the Recycle Bin, so you can put them back from there until the bin is emptied. If one is "
              + "too big for the Recycle Bin, Windows asks before deleting it for good."
            : "";
        var keys = ticked.Any(i => i.Kind == LeftoverKind.RegistryKey)
            ? @"Registry keys are saved as a .reg file first, in %LocalAppData%\SysManager\Backups\Uninstaller, "
              + "and then deleted; double-click the file to put a key back."
            : "";
        return $"{ConfirmRemovalQuestion(ticked)}\n\n{lines}\n\n{string.Join(" ", new[] { folders, keys }.Where(s => s.Length > 0))}";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand(CanExecute = nameof(HasApps))]
    private void SelectAll()
    {
        if (FilteredApps.Count > 20
            && string.IsNullOrWhiteSpace(FilterText)
            && !DialogService.Instance.Confirm(
                $"This will select all {FilteredApps.Count} applications.\n\nUse the filter to narrow down the list first.\nAre you sure you want to select all?",
                "Select all apps"))
        {
            return;
        }

        foreach (var app in FilteredApps) app.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(HasApps))]
    private void DeselectAll()
    {
        foreach (var app in FilteredApps) app.IsSelected = false;
    }

    private void ApplyFilter()
    {
        IEnumerable<InstalledApp> source = AllApps;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(a =>
                a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                a.Id.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        source = source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        FilteredApps.ReplaceWith(source);

        AppCount = FilteredApps.Count;
        Summary = $"{AppCount} apps{(AllApps.Count != AppCount ? $" (of {AllApps.Count} total)" : "")}";
    }

    /// <summary>
    /// The row status for an app whose uninstaller returned success while Windows still lists it (#2448).
    /// </summary>
    /// <remarks>
    /// "Running", not "open in its own window": through winget the uninstaller runs silently, so the copy that is
    /// still working has no window at all (#2469).
    /// </remarks>
    internal const string StillOpenStatus =
        "Still installed — its uninstaller may still be running, perhaps in its own window. Let it finish, then Scan again.";

    /// <summary>The summary's note for apps still listed after their uninstaller returned; empty when there are none.</summary>
    internal static string DescribeStillOpen(int stillOpen) => stillOpen switch
    {
        0 => string.Empty,
        1 => " 1 app is still installed — its uninstaller may still be running; Scan again once it finishes.",
        _ => $" {stillOpen} apps are still installed — their uninstallers may still be running; Scan again once they finish.",
    };

    // Windows Installer uses 1641 and 3010 for successful removal that requires a restart.
    private static bool IsSuccessfulUninstallExitCode(int exitCode) =>
        exitCode is 0 or 1641 or 3010;

    private static bool RequiresRestartAfterUninstall(int exitCode) =>
        exitCode is 1641 or 3010;

    /// <summary>
    /// Translates a winget uninstall exit code into a human-readable message so the user knows why
    /// the uninstall failed and what to try next.
    /// <para>The mapping itself moved to <see cref="WingetFailure.DescribeUninstallFailure"/>, next to
    /// the install-side one, so the three winget tabs cannot drift apart again — Bulk Installer had
    /// been writing raw exit codes while this tab explained the same numbers. This overload stays as
    /// the call site (and its tests) already read it; <paramref name="appName"/> is not part of the
    /// sentence, which is why it is unused here.</para>
    /// </summary>
    internal static string DescribeUninstallFailure(int exitCode, string appName) =>
        WingetFailure.DescribeUninstallFailure(exitCode);
}
