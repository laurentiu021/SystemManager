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
/// folders. Every change requires admin and is verified by reading the value back
/// (Tamper Protection can silently reject it); changes are confirmed first. All four changes run
/// through one funnel, which takes the shared session restore point before the first of them.
/// </summary>
public sealed partial class DefenderViewModel : ViewModelBase
{
    private readonly DefenderService _service;
    private readonly ISessionRestorePoint _restorePoint;

    public BulkObservableCollection<string> ExclusionPaths { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private bool _isTamperProtected;
    [ObservableProperty] private string _realtimeDisplay = "—";
    [ObservableProperty] private string _puaDisplay = "—";
    [ObservableProperty] private string _cfaDisplay = "—";
    [ObservableProperty] private string _mapsDisplay = "—";
    [ObservableProperty] private bool _puaEnabled;
    [ObservableProperty] private bool _cfaEnabled;
    [ObservableProperty] private string? _selectedExclusion;

    public DefenderViewModel(DefenderService service, ISessionRestorePoint restorePoint)
    {
        _service = service;
        _restorePoint = restorePoint;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Reading Defender status…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
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
            StatusMessage = !IsAvailable
                ? "Microsoft Defender is not available on this system."
                : IsTamperProtected
                    ? "Tamper Protection is ON — some changes may be ignored by Windows until you turn it off in Windows Security."
                    : "Defender status loaded.";
        }
        // GetStatusAsync runs PowerShell; a runspace-level fault (not just a script
        // RuntimeException the service catches) would otherwise escape this async
        // command unobserved. Surface it as a status message instead.
        catch (InvalidOperationException ex) { StatusMessage = $"Could not read Defender status: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not read Defender status: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task TogglePuaAsync()
    {
        if (!Confirm($"{(PuaEnabled ? "Disable" : "Enable")} potentially-unwanted-app (PUA) protection?")) return;
        // Read the target BEFORE the change runs — Apply overwrites PuaEnabled from the read-back.
        int target = PuaEnabled ? 0 : 1;
        await RunOperationAsync(() => _service.SetPuaProtectionAsync(target),
            "PUA protection", "change PUA protection", s => s.PuaProtection == target);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ToggleCfaAsync()
    {
        if (!Confirm($"{(CfaEnabled ? "Disable" : "Enable")} Controlled Folder Access (ransomware protection)?")) return;
        int target = CfaEnabled ? 0 : 1;
        await RunOperationAsync(() => _service.SetControlledFolderAccessAsync(target),
            "Controlled Folder Access", "change Controlled Folder Access",
            s => s.ControlledFolderAccess == target);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task AddExclusionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to exclude from scanning" };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FolderName;
        if (!IsValidExclusionPath(path))
        {
            StatusMessage = "That folder path is not valid.";
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
        string? path = SelectedExclusion;
        if (path is null) return;
        if (!Confirm($"Stop excluding \"{path}\"?\n\nThe folder will be scanned for malware again.")) return;

        await RunOperationAsync(() => _service.RemoveExclusionPathAsync(path),
            "Exclusion removal", "remove the exclusion",
            s => !s.ExclusionPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
    }

    private bool HasSelectedExclusion => SelectedExclusion is not null;
    // Remove must be both busy-gated and have a selection.
    private bool CanRemoveExclusion => NotBusy && HasSelectedExclusion;
    partial void OnSelectedExclusionChanged(string? value) => RemoveExclusionCommand.NotifyCanExecuteChanged();

    /// <summary>A valid exclusion is a rooted, existing folder with no wildcards.</summary>
    internal static bool IsValidExclusionPath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && Path.IsPathRooted(path)
           && !path.Contains('*') && !path.Contains('?')
           && path.Length <= 260
           && Directory.Exists(path);

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
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            // Before the change, never after. Defender preferences are machine-wide, so a snapshot
            // taken afterwards would record the settings the user is trying to be able to undo.
            var snapshotTaken = await _restorePoint
                .EnsureAsync("SysManager Defender Tweaks").ConfigureAwait(true);

            var status = await change().ConfigureAwait(true);
            Apply(status);
            ReportVerified(what, status, applied(status), snapshotTaken);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Could not {failureVerb}: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not {failureVerb}: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void Apply(DefenderStatus status)
    {
        IsAvailable = status.Available;
        IsTamperProtected = status.IsTamperProtected;
        RealtimeDisplay = status.RealtimeDisplay;
        PuaDisplay = status.PuaDisplay;
        CfaDisplay = status.CfaDisplay;
        MapsDisplay = status.MapsDisplay;
        PuaEnabled = status.PuaProtection == 1;
        CfaEnabled = status.ControlledFolderAccess == 1;
        ExclusionPaths.ReplaceWith(status.ExclusionPaths);
        RemoveExclusionCommand.NotifyCanExecuteChanged();
    }

    private void ReportVerified(string what, DefenderStatus status, bool applied, bool snapshotTaken)
    {
        // A read-back only proves anything when the status is actually readable. When the
        // Set failed (needs admin / PowerShell host fault), the service returns the all-zeros
        // DefenderStatus.Unavailable — against which a disable-toggle (target 0) or an
        // exclusion removal (empty list) would FALSELY satisfy `applied`. Treat an
        // unavailable read-back as a failure, never a silent success.
        if (applied && status.Available)
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
            StatusMessage = IsTamperProtected
                ? $"{what} was not changed — Tamper Protection blocked it. Turn it off in Windows Security first."
                : $"{what} was not changed — this needs administrator rights.";
        }
    }

    private static bool Confirm(string message)
        => DialogService.Instance.Confirm(message, "Defender — Confirm");
}
