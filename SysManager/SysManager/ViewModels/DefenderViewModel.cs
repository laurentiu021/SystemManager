// SysManager · DefenderViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Defender Tweaks tab. Shows the current Microsoft Defender status,
/// toggles PUA protection and Controlled Folder Access, and manages scan exclusion
/// folders. Every change requires admin, so without it a change is refused before anything is asked; with it,
/// the change is confirmed first and verified by reading the value back
/// (Tamper Protection can silently reject it). All four changes run
/// through one funnel, which takes the shared session restore point before the first of them.
/// </summary>
public sealed partial class DefenderViewModel : ElevatedViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly DefenderService _service;
    private readonly ISessionRestorePoint _restorePoint;
    private readonly IDialogService _dialog;

    public BulkObservableCollection<string> ExclusionPaths { get; } = new();

    /// <summary>
    /// Whether the last read showed Defender's status. False until the first read is in, and after a read that failed,
    /// so every change waits for a status it can take its question and its direction from.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExclusionsEmptyTitle), nameof(ExclusionsEmptyMessage))]
    [NotifyCanExecuteChangedFor(nameof(TogglePuaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCfaCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddExclusionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveExclusionCommand))]
    private bool _isAvailable;

    /// <summary>Whether any read has come back yet, readable or not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExclusionsEmptyTitle), nameof(ExclusionsEmptyMessage))]
    private bool _statusRead;

    [ObservableProperty] private bool _isTamperProtected;
    [ObservableProperty] private string _realtimeDisplay = DefenderStatus.NotRead;
    [ObservableProperty] private string _puaDisplay = DefenderStatus.NotRead;
    [ObservableProperty] private string _cfaDisplay = DefenderStatus.NotRead;
    [ObservableProperty] private string _mapsDisplay = DefenderStatus.NotRead;
    [ObservableProperty] private bool _puaEnabled;
    [ObservableProperty] private bool _cfaEnabled;
    [ObservableProperty] private string? _selectedExclusion;

    // Windows shows the exclusion lists only to an administrator. Without this the card said "No exclusion
    // folders" to a standard user, which is a claim about the machine the tab never checked (#2476).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExclusionsEmptyTitle), nameof(ExclusionsEmptyMessage))]
    private bool _exclusionsReadable = true;

    // Whether the list was read comes first: a status nobody read has no exclusions to show, and the card said "No
    // exclusion folders" for it.
    public string ExclusionsEmptyTitle =>
        !StatusRead ? "Reading Defender's exclusions…"
        : !IsAvailable ? "Exclusions could not be read"
        : ExclusionsReadable ? "No exclusion folders"
        : "Exclusions are hidden";

    public string ExclusionsEmptyMessage =>
        !StatusRead ? "SysManager is asking Windows for Defender's settings."
        : !IsAvailable ? "SysManager could not read Microsoft Defender's settings, so it cannot say which folders are "
                         + "excluded. Press Refresh to try again."
        : ExclusionsReadable ? "Add a folder above to exclude it from Defender scans."
        : "Windows shows Defender's exclusions only to an administrator. Use \"Run as administrator\" to see and "
          + "change them.";

    // The last status read, which every change takes its question and its direction from.
    private DefenderStatus _lastRead = DefenderStatus.Unavailable;

    // A mode PUA or Controlled Folder Access had before this tab turned it fully on, put back by the next toggle.
    private int? _puaPutBack;
    private int? _cfaPutBack;

    /// <summary>Reads and changes Microsoft Defender settings.</summary>
    /// <param name="dialog">Defaults to <see cref="DialogService.Instance"/> (#2860).</param>
    public DefenderViewModel(DefenderService service, ISessionRestorePoint restorePoint, IDialogService? dialog = null)
    {
        _service = service;
        _restorePoint = restorePoint;
        _dialog = dialog ?? DialogService.Instance;
        StatusMessage = "Reading Defender status…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }


    /// <summary>True when no Defender operation is in flight — gates the mutating
    /// commands so a user can't start a second Set-MpPreference while the first is
    /// still running (each spins its own runspace and the read-back verification
    /// could race). Mirrors the NotBusy convention used across the other VMs.</summary>
    public bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        OnPropertyChanged(nameof(NotBusy));
        RefreshCommand.NotifyCanExecuteChanged();
        TogglePuaCommand.NotifyCanExecuteChanged();
        ToggleCfaCommand.NotifyCanExecuteChanged();
        AddExclusionCommand.NotifyCanExecuteChanged();
        RemoveExclusionCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var status = await _service.GetStatusAsync().ConfigureAwait(true);
            Apply(status);
            // Whether Defender scans at all comes before the note about changes, which the Tamper Protection banner
            // above the cards also gives.
            StatusMessage = !IsAvailable
                ? "SysManager could not read Microsoft Defender's status, so nothing here can be changed. Press "
                  + "Refresh to try again."
                : status.RealtimeNote is { Length: > 0 } note
                    ? note
                    : IsTamperProtected
                        ? "Tamper Protection is ON — some changes may be ignored by Windows until you turn it off in Windows Security."
                        : "Defender status loaded.";
        }
        // GetStatusAsync runs PowerShell; a runspace-level fault (not just a script
        // RuntimeException the service catches) would otherwise escape this async
        // command unobserved. Surface it as a status message instead, over cards that no longer claim the earlier
        // read is current.
        catch (InvalidOperationException ex) { ReadFailed(ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { ReadFailed(ex.Message); }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void ReadFailed(string error)
    {
        Apply(DefenderStatus.Unavailable);
        StatusMessage = $"Could not read Defender status: {error}";
    }

    // Only 1 is fully on. From audit mode the toggle turns PUA fully on and remembers audit mode, which the next toggle
    // puts back: it counted only 1 as on, so two toggles from audit mode ended at off and the mode was lost.
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task TogglePuaAsync()
    {
        if (!RequireElevation("Changing PUA protection")) return;
        if (!RequireStatus("Changing PUA protection")) return;
        // Read the target BEFORE the change runs — Apply overwrites PuaEnabled from the read-back.
        int before = _lastRead.PuaProtection;
        int target = PuaEnabled ? _puaPutBack ?? 0 : 1;
        if (!Confirm(PuaQuestion(before, target))) return;
        await RunOperationAsync(() => _service.SetPuaProtectionAsync(target),
            "PUA protection", "change PUA protection", s => s.PuaProtection == target);
        // Remembered only once a read shows it fully on: a turn-on Windows did not take leaves nothing to put back.
        if (target == 1 && before == 2 && _lastRead is { Available: true, PuaProtection: 1 }) _puaPutBack = before;
    }

    // As PUA, for Controlled Folder Access's audit mode and its two disk-only modes.
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ToggleCfaAsync()
    {
        if (!RequireElevation("Changing Controlled Folder Access")) return;
        if (!RequireStatus("Changing Controlled Folder Access")) return;
        int before = _lastRead.ControlledFolderAccess;
        int target = CfaEnabled ? _cfaPutBack ?? 0 : 1;
        if (!Confirm(CfaQuestion(before, target))) return;
        await RunOperationAsync(() => _service.SetControlledFolderAccessAsync(target),
            "Controlled Folder Access", "change Controlled Folder Access",
            s => s.ControlledFolderAccess == target);
        if (target == 1 && IsInBetween(before) && _lastRead is { Available: true, ControlledFolderAccess: 1 })
            _cfaPutBack = before;
    }

    // Controlled Folder Access's modes other than off and fully on: audit, and the two disk-only modes.
    private static bool IsInBetween(int cfa) => cfa is >= 2 and <= 4;

    private static string PuaQuestion(int before, int target) => (before, target) switch
    {
        (2, 1) => "PUA protection is set to \"Audit mode\". Turn potentially-unwanted-app (PUA) protection fully on?"
                  + "\n\nWhile SysManager stays open, turning it off here later puts \"Audit mode\" back.",
        (_, 1) => "Enable potentially-unwanted-app (PUA) protection?",
        (_, 0) => "Disable potentially-unwanted-app (PUA) protection?",
        _ => $"Put PUA protection back to \"{DefenderStatus.TriStateLabel(target)}\", the mode it had before "
             + "SysManager turned it fully on?",
    };

    // Turning it on is the costly direction: apps Windows does not trust can no longer save into the protected
    // folders, and this tab has no way to allow one (#2505).
    private const string CfaOnConsequence =
        "Once it is on, only apps Windows trusts can change files in Documents, Pictures, Videos, Music, Favorites "
        + "and Desktop. Some games then cannot save their progress, and some programs cannot save files, until you "
        + "allow them: in Windows Security, open Virus & threat protection, then Manage ransomware protection, then "
        + "Allow an app through Controlled folder access.";

    private static string CfaQuestion(int before, int target) => (before, target) switch
    {
        (_, 1) when IsInBetween(before) =>
            $"Controlled Folder Access is set to \"{DefenderStatus.CfaLabel(before)}\". Turn it fully on (ransomware "
            + $"protection)?\n\n{CfaOnConsequence}\n\nWhile SysManager stays open, turning it off here later puts "
            + $"\"{DefenderStatus.CfaLabel(before)}\" back.",
        (_, 1) => $"Enable Controlled Folder Access (ransomware protection)?\n\n{CfaOnConsequence}",
        (_, 0) => "Disable Controlled Folder Access (ransomware protection)?\n\nApps will be able to change the files "
                  + "in your protected folders again, and so would ransomware.",
        _ => $"Put Controlled Folder Access back to \"{DefenderStatus.CfaLabel(target)}\", the mode it had before "
             + "SysManager turned it fully on?\n\nApps will be able to change the files in your protected folders "
             + "again, as they could before, and so would ransomware.",
    };

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task AddExclusionAsync()
    {
        if (!RequireElevation("Adding an exclusion")) return;
        if (!RequireStatus("Adding an exclusion")) return;
        var folder = _dialog.PickFolder("Select a folder to exclude from scanning");
        if (folder is null) return;

        string path = folder;
        // The service's rule, so the two cannot drift apart; this tab adds only that the folder must exist (#2773).
        if (DefenderService.ExclusionRefusal(path) is { } why)
        {
            StatusMessage = $"SysManager won't exclude \"{path}\" from scanning: it {why}, so the files in it would no "
                + "longer be checked for malware.";
            return;
        }
        if (!Directory.Exists(path))
        {
            StatusMessage = $"\"{path}\" was not found, so it was not excluded.";
            return;
        }
        if (!Confirm($"Exclude \"{path}\" from Defender scanning?\n\nFiles in an excluded folder are not scanned for malware.")) return;

        await RunOperationAsync(() => _service.AddExclusionPathAsync(path),
            "Exclusion", "add the exclusion",
            s => s.ExclusionPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand(CanExecute = nameof(CanRemoveExclusion))]
    private async Task RemoveExclusionAsync()
    {
        if (!RequireElevation("Removing an exclusion")) return;
        if (!RequireStatus("Removing an exclusion")) return;
        string? path = SelectedExclusion;
        if (path is null) return;
        if (!Confirm($"Stop excluding \"{path}\"?\n\nThe folder will be scanned for malware again.")) return;

        await RunOperationAsync(() => _service.RemoveExclusionPathAsync(path),
            "Exclusion removal", "remove the exclusion",
            s => !s.ExclusionPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
    }

    private bool HasSelectedExclusion => SelectedExclusion is not null;
    // A change needs a status that was read, and no other change in flight.
    private bool CanChange => NotBusy && IsAvailable;
    // Remove must also have a selection.
    private bool CanRemoveExclusion => CanChange && HasSelectedExclusion;

    /// <summary>
    /// Whether the last read showed Defender's status, saying so when it did not.
    /// </summary>
    /// <remarks>
    /// Each change takes its question and its direction from that read, so without it the question came from values
    /// nobody read, after a snapshot was already taken. Checked in the body as well as by CanExecute, as
    /// RequireElevation is, because ExecuteAsync runs the body whatever CanExecute says.
    /// </remarks>
    private bool RequireStatus(string what)
    {
        if (IsAvailable) return true;
        StatusMessage = $"{what} needs Defender's status, and SysManager has not been able to read it. Press Refresh "
            + "to try again.";
        return false;
    }
    partial void OnSelectedExclusionChanged(string? value) => RemoveExclusionCommand.NotifyCanExecuteChanged();

    /// <summary>A valid exclusion is one the service allows (<see cref="DefenderService.ExclusionRefusal(string)"/>) and an existing folder.</summary>
    internal static bool IsValidExclusionPath(string path)
        => DefenderService.IsValidExclusionPath(path) && Directory.Exists(path);

    /// <summary>
    /// The shape all four Defender changes share: gate the UI, take the session restore point before
    /// the first change of the session, run the change, adopt the returned status and report it
    /// against a read-back.
    /// <para>One funnel rather than four copies because the restore point belongs in exactly one
    /// place — four private calls is the duplication that made Tweaks Hub and Gaming Profile each
    /// burn the 24-hour allowance, with the loser reporting no snapshot while a good one existed.
    /// The failure wording is passed in so each command keeps the message it already had.</para>
    /// </summary>
    private async Task RunOperationAsync(
        Func<Task<DefenderStatus>> change,
        string what,
        string failureVerb,
        Func<DefenderStatus, bool> applied)
    {
        // A change that is cancelled with nothing to name it when SysManager closes mid-run. One funnel
        // for all four changes, so the lock cannot end up guarding some of them and not others (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Defender Tweaks");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        var changeStarted = false;
        try
        {
            // Before the change, never after. Defender preferences are machine-wide, so a snapshot
            // taken afterwards would record the settings the user is trying to be able to undo.
            var snapshotTaken = await _restorePoint
                .EnsureAsync("SysManager Defender Tweaks").ConfigureAwait(true);

            changeStarted = true;
            var status = await change().ConfigureAwait(true);
            Apply(status);
            ReportVerified(what, failureVerb, status, applied(status), snapshotTaken);
        }
        catch (InvalidOperationException ex) { ChangeFailed(failureVerb, changeStarted, ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { ChangeFailed(failureVerb, changeStarted, ex.Message); }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // A fault once the change has started may come from the change or from the read after it, so whether Windows took
    // it is unknown, and the cards no longer show what is set.
    private void ChangeFailed(string failureVerb, bool changeStarted, string error)
    {
        if (!changeStarted)
        {
            StatusMessage = $"Could not {failureVerb}: {error}";
            return;
        }
        Log.Debug("Defender: could not {Verb} or read it back: {Error}", failureVerb, error);
        Apply(DefenderStatus.Unavailable);
        StatusMessage = OutcomeUnknownMessage(failureVerb);
    }

    private static string OutcomeUnknownMessage(string failureVerb) =>
        $"SysManager could not read Defender's settings back, so it cannot tell whether it managed to {failureVerb}. "
        + "Press Refresh to see.";

    private void Apply(DefenderStatus status)
    {
        _lastRead = status;
        // A remembered mode belongs to the protection this tab turned fully on: a read that shows it set any other way,
        // put back here or changed in Windows Security, ends it.
        if (status.Available && status.PuaProtection != 1) _puaPutBack = null;
        if (status.Available && status.ControlledFolderAccess != 1) _cfaPutBack = null;
        StatusRead = true;
        IsAvailable = status.Available;
        IsTamperProtected = status.IsTamperProtected;
        RealtimeDisplay = status.RealtimeDisplay;
        PuaDisplay = status.PuaDisplay;
        CfaDisplay = status.CfaDisplay;
        MapsDisplay = status.MapsDisplay;
        PuaEnabled = status.PuaProtection == 1;
        CfaEnabled = status.ControlledFolderAccess == 1;
        ExclusionsReadable = status.ExclusionsReadable;
        ExclusionPaths.ReplaceWith(status.ExclusionPaths);
        RemoveExclusionCommand.NotifyCanExecuteChanged();
    }

    private void ReportVerified(
        string what, string failureVerb, DefenderStatus status, bool applied, bool snapshotTaken)
    {
        // A read-back only proves anything when the status is actually readable. The status of a read that failed
        // is all zeros, against which a disable-toggle (target 0) or an exclusion removal (empty list) would FALSELY
        // satisfy `applied`, and it shows no change either way, so it is neither success nor failure: the change
        // may well have been made.
        if (!status.Available)
        {
            StatusMessage = OutcomeUnknownMessage(failureVerb);
        }
        else if (applied)
        {
            // Mentioned only when Windows really made one — the rule Tweaks Hub set. System Restore
            // is switched off on many PCs and rate-limited to about one point a day, so silence here
            // means "no snapshot", never a promise. Not mentioned on the failure paths below: those
            // changed nothing, so there is nothing for a snapshot to reassure the user about.
            var rp = snapshotTaken ? " Restore point created." : "";
            StatusMessage = $"{what} updated.{rp}";
            Log.Information("Defender: {What} change applied", what);
        }
        else
        {
            StatusMessage = NotChangedMessage(what, IsElevated, IsTamperProtected);
        }
    }

    /// <summary>What the status line says when the read-back does not show the change.</summary>
    /// <remarks>
    /// Elevation comes first. Windows lets a standard user read Defender but not change it, and on a default install
    /// that read says Tamper Protection is on, so asking about it first blamed the protection, and told the user to
    /// turn it off, for a refusal that was about rights (#2730). The commands now refuse before asking anything when
    /// SysManager is not elevated, so this is the second check, not the only one.
    /// </remarks>
    internal static string NotChangedMessage(string what, bool elevated, bool tamperProtected) => (elevated, tamperProtected) switch
    {
        (false, _) => $"{what} was not changed — this needs administrator rights. Use \"Run as administrator\" above.",
        (true, true) => $"{what} was not changed — Tamper Protection blocked it. Turn it off in Windows Security first.",
        (true, false) => $"{what} was not changed — Windows did not apply it.",
    };

    // Every change here runs through RunOperationAsync, which takes the session restore point first, so every
    // confirmation says what that attempt does to System Protection when it will make one (#2483).
    private bool Confirm(string message)
        => DialogService.Instance.Confirm(message + _restorePoint.ConfirmationNotice, "Defender — Confirm");
}
