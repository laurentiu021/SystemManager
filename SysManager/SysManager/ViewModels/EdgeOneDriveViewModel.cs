// SysManager · EdgeOneDriveViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Edge/OneDrive Remover tab. Surfaces the current integration state and
/// offers reversible actions: OneDrive is fully removable per-user (no admin), while Edge is
/// only ever "disabled &amp; de-integrated" (background/startup-boost policy + auto-update tasks),
/// never uninstalled — with a matching Restore for each. Changing the default browser can't be
/// done programmatically, so the tab guides the user to Windows Settings. Every action confirms
/// first and reports its honest outcome.
/// </summary>
public sealed partial class EdgeOneDriveViewModel : ElevatedViewModelBase
{
    private readonly EdgeOneDriveService _service;
    // Removing OneDrive or de-integrating Edge is among the scariest things in the app for the
    // target user, and until now it was one of the tabs with NO snapshot while the identical
    // registry writes made through Tweaks Hub did get one. Shared so the whole session takes at
    // most one point.
    private readonly ISessionRestorePoint _restorePoint;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneDriveStateText))]
    private bool _oneDriveInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneDriveStateText))]
    private bool _oneDriveRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EdgeStateText))]
    private bool _edgeInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EdgeStateText))]
    private bool _edgeBackgroundDisabled;

    /// <summary>
    /// True once a read of the state has worked. Until then the four flags above are only their defaults, and the card
    /// said "OneDrive is not installed." while it was still looking (#2810).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneDriveStateText), nameof(EdgeStateText))]
    private bool _statusRead;

    /// <summary>True when the first read failed, so the card says so rather than that it is still checking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OneDriveStateText), nameof(EdgeStateText))]
    private bool _statusUnreadable;

    public string OneDriveStateText => UnreadState("OneDrive") ?? (!OneDriveInstalled
        ? "OneDrive is not installed."
        : OneDriveRunning ? "OneDrive is installed and running." : "OneDrive is installed.");

    public string EdgeStateText => UnreadState("Microsoft Edge") ?? (!EdgeInstalled
        ? "Microsoft Edge is not installed."
        : EdgeBackgroundDisabled
            ? "Edge is de-integrated (background mode and startup boost are off)."
            : "Edge is active (background mode / startup boost may be on).");

    /// <summary>What Edge says while it is disabled and why, on the Edge card (#2880).</summary>
    public string EdgeManagedNotice => EdgeOneDriveService.ManagedBrowserNotice;

    /// <summary>What a card says before a read has worked, or null once one has.</summary>
    private string? UnreadState(string component) => StatusRead ? null
        : StatusUnreadable ? $"{component}'s state could not be read." : $"Checking {component}…";

    public EdgeOneDriveViewModel(EdgeOneDriveService service, ISessionRestorePoint restorePoint)
    {
        _service = service;
        _restorePoint = restorePoint;
        StatusMessage = "Reading Edge and OneDrive status…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    /// <summary>True when no operation is in flight — gates every mutating command so a second
    /// action can't start while the first is still running.</summary>
    public bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        OnPropertyChanged(nameof(NotBusy));
        RefreshCommand.NotifyCanExecuteChanged();
        RemoveOneDriveCommand.NotifyCanExecuteChanged();
        RestoreOneDriveCommand.NotifyCanExecuteChanged();
        DisableEdgeCommand.NotifyCanExecuteChanged();
        RestoreEdgeCommand.NotifyCanExecuteChanged();
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
            StatusMessage = "Status loaded.";
        }
        // GetStatusAsync runs PowerShell; a runspace-level fault (not the RuntimeException the
        // service catches) would otherwise escape this async command unobserved.
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"Could not read status: {ex.Message}";
            StatusUnreadable = true;
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RemoveOneDriveAsync()
    {
        if (StatusRead && !OneDriveInstalled)
        {
            StatusMessage = "OneDrive is not installed — nothing to remove.";
            return;
        }
        if (!Confirm(
                "Remove OneDrive for your account?\n\n" +
                "This stops OneDrive, uninstalls it for the current user, and removes its File Explorer " +
                "sidebar entry. Your files already synced to this PC stay on disk; files that live only " +
                "in the cloud won't be downloaded. You can reinstall OneDrive from this tab at any time.",
                "Remove OneDrive"))
        {
            StatusMessage = "OneDrive removal cancelled.";
            return;
        }
        var wasRunning = OneDriveRunning;
        var outcome = await RunOperationAsync(_service.RemoveOneDriveAsync, "OneDrive", "removed").ConfigureAwait(true);
        // The uninstaller runs with OneDrive closed, and a failed removal leaves it closed (#2810). It is not started
        // again from here: OneDrive refuses to run with the administrator rights SysManager may have, and one started
        // through the PowerShell helper can be ended with it. Checked after the refresh, so one running again gets no
        // note.
        if (outcome == EdgeOneDriveOutcome.Failed && wasRunning && OneDriveInstalled && !OneDriveRunning)
            StatusMessage += " It was closed for the attempt: open OneDrive from the Start menu to start it again.";
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RestoreOneDriveAsync()
    {
        if (!Confirm("Reinstall OneDrive for your account and restore its File Explorer sidebar entry?",
                "Restore OneDrive"))
        {
            StatusMessage = "OneDrive restore cancelled.";
            return;
        }
        await RunOperationAsync(_service.RestoreOneDriveAsync, "OneDrive", "restored").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DisableEdgeAsync()
    {
        if (StatusRead && !EdgeInstalled)
        {
            StatusMessage = "Microsoft Edge is not installed — nothing to change.";
            return;
        }
        if (!Confirm(
                "Disable and de-integrate Microsoft Edge?\n\n" +
                "Edge is never uninstalled — Windows needs it and would reinstall it anyway. This turns " +
                "off Edge's background mode and startup boost and disables its automatic-update tasks, so " +
                "it stops running on its own. Without those tasks Edge no longer updates itself in the " +
                "background, so its security fixes can arrive late until you restore it. You can still " +
                "open Edge normally, and you can undo all of " +
                "this from the Restore button. This needs administrator rights.\n\n" +
                EdgeOneDriveService.ManagedBrowserNotice,
                "Disable & de-integrate Edge"))
        {
            StatusMessage = "Edge change cancelled.";
            return;
        }
        await RunOperationAsync(_service.DisableEdgeAsync, "Edge", "de-integrated").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RestoreEdgeAsync()
    {
        if (!Confirm(
                "Restore Microsoft Edge to its Windows defaults?\n\n" +
                "This clears the background-mode and startup-boost policies, which also ends Edge's \"" +
                EdgeOneDriveService.ManagedBrowserMessage + "\" message unless something else sets Edge " +
                "policies too, and re-enables Edge's automatic-update tasks. This needs administrator rights.",
                "Restore Edge"))
        {
            StatusMessage = "Edge restore cancelled.";
            return;
        }
        await RunOperationAsync(_service.RestoreEdgeAsync, "Edge", "restored").ConfigureAwait(true);
    }

    /// <summary>
    /// Hands off to the Windows "Default apps" Settings page so the user can pick their browser.
    /// SysManager never changes the default browser itself — the UserChoice association is
    /// hash-protected on modern Windows, so any programmatic change would be rejected or reverted.
    /// </summary>
    [RelayCommand]
    private void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })?.Dispose();
            StatusMessage = "Opened Windows default-apps settings — pick your preferred browser there.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Could not open default-apps settings: {Error}", ex.Message);
            StatusMessage = "Couldn't open Windows default-apps settings.";
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Could not open default-apps settings: {Error}", ex.Message);
            StatusMessage = "Couldn't open Windows default-apps settings.";
        }
    }

    /// <summary>
    /// Shared runner for the four mutating operations: flips busy, invokes the service, maps the
    /// honest <see cref="EdgeOneDriveOutcome"/> to a status message, logs a successful change, and
    /// always re-reads the status so the panel reflects reality. Returns the outcome, or null when the change did not
    /// run or ended in an exception.
    /// </summary>
    private async Task<EdgeOneDriveOutcome?> RunOperationAsync(
        Func<CancellationToken, Task<EdgeOneDriveOutcome>> operation, string component, string pastTense)
    {
        // A change that is cancelled with nothing to name it when SysManager closes mid-run. One funnel
        // for all four changes, so the lock cannot end up guarding some of them and not others (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Edge/OneDrive Remover");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return null;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        EdgeOneDriveOutcome? result = null;
        try
        {
            // Before the change, never after: a snapshot taken afterwards would record the state
            // the user is trying to get back FROM. Applies to the Restore commands too — putting
            // Edge back is a system change like any other.
            var snapshotTaken = await _restorePoint
                .EnsureAsync("SysManager Edge/OneDrive", CancellationToken.None).ConfigureAwait(true);

            var outcome = await operation(CancellationToken.None).ConfigureAwait(true);
            result = outcome;
            StatusMessage = outcome switch
            {
                EdgeOneDriveOutcome.Success => snapshotTaken
                    ? $"{component} {pastTense}. Restore point created."
                    : $"{component} {pastTense}.",
                EdgeOneDriveOutcome.NeedsAdmin => $"{component} was not changed — this needs administrator rights. Use \"Run as administrator\" above.",
                EdgeOneDriveOutcome.NotApplicable => $"{component} is not installed — nothing to do.",
                _ => $"{component} could not be {pastTense}.",
            };
            if (outcome == EdgeOneDriveOutcome.Success)
            {
                Log.Information("EdgeOneDrive: {Component} {PastTense}", component, pastTense);
                ActivityLogService.Instance.Log("Edge/OneDrive", $"{component} {pastTense}");
            }
        }
        catch (InvalidOperationException ex) { StatusMessage = $"{component} operation failed: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"{component} operation failed: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            // Re-read so the status panel matches the new reality, regardless of outcome.
            await RefreshAfterOperationAsync().ConfigureAwait(true);
        }
        return result;
    }

    private async Task RefreshAfterOperationAsync()
    {
        try
        {
            var status = await _service.GetStatusAsync().ConfigureAwait(true);
            Apply(status);
        }
        catch (InvalidOperationException ex) { Log.Debug("EdgeOneDrive: post-op refresh failed: {Error}", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug("EdgeOneDrive: post-op refresh failed: {Error}", ex.Message); }
    }

    private void Apply(EdgeOneDriveStatus status)
    {
        OneDriveInstalled = status.OneDriveInstalled;
        OneDriveRunning = status.OneDriveRunning;
        EdgeInstalled = status.EdgeInstalled;
        EdgeBackgroundDisabled = status.EdgeBackgroundDisabled;
        StatusRead = true;
    }

    // All four changes run through RunOperationAsync, which takes the session restore point first, so every
    // confirmation says what that attempt does to System Protection when it will make one (#2483).
    private bool Confirm(string message, string title)
        => DialogService.Instance.Confirm(message + _restorePoint.ConfirmationNotice, title);

    protected override void Dispose(bool disposing)
    {
        if (disposing) PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }
}
