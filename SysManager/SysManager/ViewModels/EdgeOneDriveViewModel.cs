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
public sealed partial class EdgeOneDriveViewModel : ViewModelBase
{
    private readonly EdgeOneDriveService _service;

    [ObservableProperty] private bool _isElevated;

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

    public string OneDriveStateText => !OneDriveInstalled
        ? "OneDrive is not installed."
        : OneDriveRunning ? "OneDrive is installed and running." : "OneDrive is installed.";

    public string EdgeStateText => !EdgeInstalled
        ? "Microsoft Edge is not installed."
        : EdgeBackgroundDisabled
            ? "Edge is de-integrated (background mode and startup boost are off)."
            : "Edge is active (background mode / startup boost may be on).";

    public EdgeOneDriveViewModel(EdgeOneDriveService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
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

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
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
        catch (InvalidOperationException ex) { StatusMessage = $"Could not read status: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not read status: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RemoveOneDriveAsync()
    {
        if (!OneDriveInstalled) { StatusMessage = "OneDrive is not installed — nothing to remove."; return; }
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
        await RunOperationAsync(_service.RemoveOneDriveAsync, "OneDrive", "removed").ConfigureAwait(true);
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
        if (!EdgeInstalled) { StatusMessage = "Microsoft Edge is not installed — nothing to change."; return; }
        if (!Confirm(
                "Disable and de-integrate Microsoft Edge?\n\n" +
                "Edge is never uninstalled — Windows needs it and would reinstall it anyway. This turns " +
                "off Edge's background mode and startup boost and disables its automatic-update tasks, so " +
                "it stops running on its own. You can still open Edge normally, and you can undo all of " +
                "this from the Restore button. This needs administrator rights.",
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
                "This clears the background-mode and startup-boost policies and re-enables Edge's " +
                "automatic-update tasks. This needs administrator rights.",
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
    /// always re-reads the status so the panel reflects reality.
    /// </summary>
    private async Task RunOperationAsync(Func<CancellationToken, Task<EdgeOneDriveOutcome>> operation, string component, string pastTense)
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var outcome = await operation(CancellationToken.None).ConfigureAwait(true);
            StatusMessage = outcome switch
            {
                EdgeOneDriveOutcome.Success => $"{component} {pastTense}.",
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
    }

    private static bool Confirm(string message, string title)
        => DialogService.Instance.Confirm(message, title);

    protected override void Dispose(bool disposing)
    {
        if (disposing) PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }
}
